// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using StackExchange.Redis;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Reclaims index members whose payload expired, including indexes left by earlier processes.
/// Owns one serialized cursor traversal; cache writes retain their existing atomic transaction.
/// </summary>
internal sealed partial class RedisCacheIndexMaintenance : IAsyncDisposable
{
    private const int PageSize = 128;
    private const int DiscoveryCommandsPerTick = 16;
    private const int MaxCommandsPerTick = 128;
    private const string IndexSuffix = ":__cache_key_index__";
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    // The existence check and removal must be one Redis operation: a separate EXISTS
    // followed by SREM could discard the membership of a concurrent renewed value.
    internal const string RemoveExpiredMembersScript = """
        local removed = 0
        for i = 2, #KEYS do
            if redis.call('EXISTS', KEYS[i]) == 0 then
                removed = removed + redis.call('SREM', KEYS[1], KEYS[i])
            end
        end
        return removed
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _database;
    private readonly ILogger _logger;
    private readonly string _pattern;
    private readonly string _prefix;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _loop;
    private readonly Queue<RedisKey> _indexes = new();
    private readonly Queue<RedisValue> _members = new();
    private IServer? _server;
    private int _nextEndpoint;
    private string _serverCursor = "0";
    private string _memberCursor = "0";
    private RedisKey _index;
    private bool _discoveryCompleted;

    public RedisCacheIndexMaintenance(
        IConnectionMultiplexer redis,
        string prefix,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _redis = redis;
        _database = redis.GetDatabase();
        _prefix = prefix;
        _pattern = EscapePattern(prefix) + "scope:*" + IndexSuffix;
        _logger = logger;

        // A singleton may first be resolved during a request. Its maintenance loop
        // must not inherit that request's Activity, schema or other AsyncLocal state.
        if (ExecutionContext.IsFlowSuppressed())
        {
            _loop = RunAsync(timeProvider);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                _loop = RunAsync(timeProvider);
            }
        }
    }

    private async Task RunAsync(TimeProvider timeProvider)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    _discoveryCompleted = false;
                    // SCAN COUNT is a Redis hint, not a strict response-size cap.
                    // Bound commands and script arguments, carrying page leftovers
                    // forward rather than materializing an entire index with SMEMBERS.
                    for (var command = 0; command < MaxCommandsPerTick; command++)
                    {
                        // Spend the larger budget only on indexes already found.
                        // Idle database discovery stays cheap; active backlogs can
                        // drain up to roughly 8K members per tick with normal pages.
                        if (command >= DiscoveryCommandsPerTick && _index.Equals(default(RedisKey)) && _indexes.Count == 0)
                        {
                            break;
                        }
                        _stopping.Token.ThrowIfCancellationRequested();
                        if (!await SweepStepAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A disappeared/retyped index or unavailable endpoint must not
                    // stop future maintenance or change foreground fallback policy.
                    _index = default;
                    _members.Clear();
                    _memberCursor = "0";
                    _server = null;
                    _serverCursor = "0";
                    Log.SweepFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Normal service disposal.
        }
    }

    private async Task<bool> SweepStepAsync()
    {
        if (_index.Equals(default(RedisKey)))
        {
            if (!_indexes.TryDequeue(out _index))
            {
                if (_discoveryCompleted)
                {
                    return false;
                }
                await DiscoverIndexesAsync().ConfigureAwait(false);
                return true;
            }
            _memberCursor = "0";
        }

        if (_members.Count > 0)
        {
            var keys = new RedisKey[Math.Min(PageSize, _members.Count) + 1];
            keys[0] = _index;
            for (var i = 1; i < keys.Length; i++)
            {
                keys[i] = _members.Dequeue().ToString();
            }
            // Declare every accessed key for routing/ACL validation. As with the
            // existing payload+index write transaction, Redis Cluster deployments
            // require a shared hash tag in the configured storage/key prefix.
            await _database.ScriptEvaluateAsync(RemoveExpiredMembersScript, keys).ConfigureAwait(false);
            if (_members.Count == 0 && _memberCursor == "0")
            {
                _index = default;
            }
            return true;
        }

        var result = ReadScanResult(await _database.ExecuteAsync(
            "SSCAN", _index, _memberCursor, "COUNT", PageSize).ConfigureAwait(false));
        _memberCursor = result.Cursor;
        foreach (var member in result.Items)
        {
            _members.Enqueue((RedisValue)member);
        }
        if (_members.Count == 0 && _memberCursor == "0")
        {
            _index = default;
        }
        return true;
    }

    private async Task DiscoverIndexesAsync()
    {
        if (_server is null)
        {
            var endpoints = _redis.GetEndPoints();
            if (endpoints.Length == 0)
            {
                _discoveryCompleted = true;
                return;
            }
            var endpointIndex = _nextEndpoint < endpoints.Length ? _nextEndpoint : 0;
            _nextEndpoint = (endpointIndex + 1) % endpoints.Length;
            _server = _redis.GetServer(endpoints[endpointIndex]);
            if (!_server.IsConnected || _server.IsReplica || _server.ServerType == ServerType.Sentinel)
            {
                _server = null;
                _discoveryCompleted = true;
                return;
            }
        }

        var result = ReadScanResult(await _server.ExecuteAsync(
            _database.Database, "SCAN", [_serverCursor, "MATCH", _pattern, "COUNT", PageSize, "TYPE", "set"],
            CommandFlags.None).ConfigureAwait(false));
        _serverCursor = result.Cursor;
        // codeql[cs/linq/missed-where]: enqueues only the scope index keys on this scan page
        foreach (var value in result.Items.Select(key => (string?)key))
        {
            if (value is not null && value.StartsWith(_prefix + "scope:", StringComparison.Ordinal) &&
                value.EndsWith(IndexSuffix, StringComparison.Ordinal))
            {
                _indexes.Enqueue((RedisKey)value);
            }
        }
        if (_serverCursor == "0")
        {
            _server = null;
            // Finish pending indexes but do not rediscover this small database
            // repeatedly in the same tick. The next tick visits the next endpoint.
            _discoveryCompleted = true;
        }
    }

    private static (string Cursor, RedisResult[] Items) ReadScanResult(RedisResult response)
    {
        var result = (RedisResult[]?)response;
        if (result is not { Length: 2 } || (string?)result[0] is not { } cursor ||
            (RedisResult[]?)result[1] is not { } items)
        {
            throw new InvalidOperationException("Invalid Redis scan response.");
        }
        return (cursor, items);
    }

    private static string EscapePattern(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '*' or '?' or '[' or ']' or '\\')
            {
                result.Append('\\');
            }
            result.Append(character);
        }
        return result.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        // Await an already-issued Redis operation too; disposal must not leave a
        // command/continuation retaining this service after its owner has stopped.
        await _loop.ConfigureAwait(false);
        _stopping.Dispose();
        _indexes.Clear();
        _members.Clear();
        _index = default;
        _server = null;
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Warning, "Redis cache index maintenance failed; retrying on the next tick")]
        public static partial void SweepFailed(ILogger logger, Exception exception);
    }
}
