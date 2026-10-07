// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Threading.Channels;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>A shared, manually advanced clock for reservation timers and Redis expiry.</summary>
internal sealed class GrpcIdempotencyTestClock : TimeProvider
{
    private readonly ConcurrentBag<DelayTimer> _timers = [];
    private readonly ConcurrentDictionary<TimeSpan, Channel<DelayTimer>> _scheduled = new();
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _timestamp);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new NotSupportedException("Only one-shot reservation delays are expected.");
        }

        var timer = new DelayTimer(callback, state, GetTimestamp() + dueTime.Ticks);
        _timers.Add(timer);
        _scheduled.GetOrAdd(dueTime, _ => Channel.CreateUnbounded<DelayTimer>()).Writer.TryWrite(timer);
        return timer;
    }

    /// <summary>Waits for the continuation to finish renewing and schedule its next delay.</summary>
    public async Task WaitForDelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        // Task.Delay truncates its TimeSpan to whole milliseconds before CreateTimer.
        delay = TimeSpan.FromMilliseconds((long)delay.TotalMilliseconds);
        var channel = _scheduled.GetOrAdd(delay, _ => Channel.CreateUnbounded<DelayTimer>());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var timer = await channel.Reader.ReadAsync(timeout.Token);
            if (timer.IsPending)
            {
                return;
            }
        }
    }

    public async Task<T> CompletePollingAsync<T>(Task<T> response, TimeSpan pollInterval)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!response.IsCompleted)
        {
            var scheduled = WaitForDelayAsync(pollInterval, stop.Token);
            if (await Task.WhenAny(response, scheduled) == response)
            {
                await stop.CancelAsync();
                try
                {
                    await scheduled;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // The response won the race; cancel the unused delay and observe its cancellation.
                    break;
                }

                break;
            }

            await scheduled;
            Advance(pollInterval);
        }

        return await response;
    }

    public void Advance(TimeSpan elapsed)
    {
        var now = Interlocked.Add(ref _timestamp, elapsed.Ticks);
        foreach (var timer in _timers)
        {
            timer.FireIfDue(now);
        }
    }

    private sealed class DelayTimer(TimerCallback callback, object? state, long dueAt) : ITimer
    {
        private int _status;

        public bool IsPending => Volatile.Read(ref _status) == 0;

        public void FireIfDue(long now)
        {
            if (now >= dueAt && Interlocked.CompareExchange(ref _status, 1, 0) == 0)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Interlocked.Exchange(ref _status, 2);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Executes the production Lua scripts on real Redis with expiry driven by the same clock
/// as the writer. Only Redis's wall clock is replaced; token comparison, NX acquisition,
/// renewal and receipt publication still execute the unmodified production scripts.
/// </summary>
internal sealed class GrpcIdempotencyClockedRedis
{
    private const string ExpirySuffix = ":test-clock-expiry";
    private const string ClockScript = """
        local now = tonumber(table.remove(ARGV))
        local call = redis.call
        local redis = {}
        function redis.call(command, key, ...)
          local expiryKey = key .. ':test-clock-expiry'
          local expiry = call('GET', expiryKey)
          if expiry and tonumber(expiry) <= now then call('DEL', key, expiryKey) end
          local args = {...}
          if command == 'SET' then
            local ttl = nil
            local i = 1
            while i <= #args do
              if args[i] == 'PX' then
                ttl = tonumber(args[i + 1])
                table.remove(args, i)
                table.remove(args, i)
              else i = i + 1 end
            end
            local result = call('SET', key, unpack(args))
            if result and ttl then call('SET', expiryKey, now + ttl) end
            return result
          elseif command == 'PEXPIRE' then
            if call('EXISTS', key) == 0 then return 0 end
            call('SET', expiryKey, now + tonumber(args[1]))
            return 1
          elseif command == 'DEL' then
            call('DEL', expiryKey)
            return call('DEL', key)
          elseif command == 'GET' then
            return call('GET', key)
          end
          error('Unexpected reservation command: ' .. command)
        end

        """;

    private readonly IDatabase _database;
    private readonly GrpcIdempotencyTestClock _clock;
    private string? _key;

    public GrpcIdempotencyClockedRedis(IConnectionMultiplexer real, GrpcIdempotencyTestClock clock)
    {
        _database = real.GetDatabase();
        _clock = clock;
        var database = Substitute.For<IDatabase>();
        database
            .ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var keys = call.ArgAt<RedisKey[]>(1);
                Volatile.Write(ref _key, keys[0].ToString());
                var args = call.ArgAt<RedisValue[]>(2)
                    .Append((RedisValue)(clock.GetTimestamp() / TimeSpan.TicksPerMillisecond)).ToArray();
                return _database.ScriptEvaluateAsync(
                    ClockScript + "\n" + call.ArgAt<string>(0), keys, args, call.ArgAt<CommandFlags>(3));
            });
        Connection = Substitute.For<IConnectionMultiplexer>();
        Connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
    }

    public IConnectionMultiplexer Connection { get; }

    public RedisKey Key => Volatile.Read(ref _key) ?? throw new InvalidOperationException("No reservation acquired.");

    public async Task<TimeSpan> RemainingLifetimeAsync()
    {
        var expiry = (long)await _database.StringGetAsync(Key.Append(ExpirySuffix));
        return TimeSpan.FromMilliseconds(expiry - (_clock.GetTimestamp() / TimeSpan.TicksPerMillisecond));
    }
}
