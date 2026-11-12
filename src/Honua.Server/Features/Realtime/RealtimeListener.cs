// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Features.Realtime;

// The one dedicated, non-pooled connection per serving task that waits for realtime notifications
// (specification section 6.2, the ADR-0082 exception). It runs LISTEN for every family and then
// only waits; it never opens a transaction. A notification only wakes the dispatcher, which reads
// the log, so a notification lost while disconnected costs latency, never an event.
internal sealed partial class RealtimeListener : IAsyncDisposable
{
    public const string ApplicationName = "honua-realtime-listener";

    private readonly NpgsqlDataSource? _dataSource;
    private readonly IOptions<RealtimeOptions> _options;
    private readonly ILogger<RealtimeListener> _logger;
    private volatile bool _connected;
    private long _reconnects;
    private int _processId;

    private RealtimeListener(NpgsqlDataSource? dataSource, string? pollOnlyReason, IOptions<RealtimeOptions> options, ILogger<RealtimeListener> logger)
    {
        _dataSource = dataSource;
        PollOnlyReason = pollOnlyReason;
        _options = options;
        _logger = logger;
    }

    // Why this task runs without a listener, or null when it has one.
    public string? PollOnlyReason { get; }

    public bool IsConnected => _connected;

    public long Reconnects => Interlocked.Read(ref _reconnects);

    // The backend process id of the open listener connection, or 0.
    public int ProcessId => Volatile.Read(ref _processId);

    // Builds the listener from ConnectionStrings:RealtimeListener when it is set, otherwise from the
    // system database's resolved connection string (secret references already resolved). A
    // connection-proxy host cannot hold LISTEN across transactions, so it gets no listener; neither
    // does a connection string that cannot be parsed. Either way the task runs on the degraded poll.
    public static RealtimeListener Create(
        string? listenerConnectionString,
        string? systemConnectionString,
        IOptions<RealtimeOptions> options,
        ILogger<RealtimeListener> logger)
    {
        var connectionString = string.IsNullOrWhiteSpace(listenerConnectionString) ? systemConnectionString : listenerConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new RealtimeListener(null, "no connection string is configured", options, logger);
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                KeepAlive = 15,
                TcpKeepAlive = true,
                ApplicationName = ApplicationName,
                Multiplexing = false
            };
        }
        catch (ArgumentException exception)
        {
            LogUnusableConnectionString(logger, exception);
            return new RealtimeListener(null, "the connection string could not be parsed", options, logger);
        }

        if (builder.Host?.Contains(".proxy-", StringComparison.OrdinalIgnoreCase) == true)
        {
            LogProxyHost(logger);
            return new RealtimeListener(null, "the database host is a connection proxy", options, logger);
        }

        return new RealtimeListener(NpgsqlDataSource.Create(builder.ConnectionString), null, options, logger);
    }

    // Holds the listener open until cancelled, reconnecting with backoff from
    // ListenerReconnectMinDelay to ListenerReconnectMaxDelay with jitter. Every (re)connect wakes
    // the dispatcher once, which closes the window in which notifications were missed.
    public async Task RunAsync(Action wake, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wake);
        if (_dataSource is null)
        {
            return;
        }

        var options = _options.Value;
        var delay = options.ListenerReconnectMinDelay;
        var failed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                connection.Notification += (_, _) => wake();
                foreach (var family in RealtimeLogFamilies.Logged.Append(RealtimeLogFamilies.Features))
                {
                    await using var listen = new NpgsqlCommand($"LISTEN \"{Channel(family)}\"", connection);
                    await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                Volatile.Write(ref _processId, connection.ProcessID);
                _connected = true;
                if (failed)
                {
                    Interlocked.Increment(ref _reconnects);
                    LogReconnected(_logger);
                }

                failed = false;
                delay = options.ListenerReconnectMinDelay;
                wake();
                while (true)
                {
                    await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _connected = false;
                Volatile.Write(ref _processId, 0);
                failed = true;
                LogDisconnected(_logger, delay, exception);
                wake();
            }

            try
            {
                await Task.Delay(Jitter(delay), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.ListenerReconnectMaxDelay.Ticks));
        }

        _connected = false;
        Volatile.Write(ref _processId, 0);
    }

    public static string Channel(string family) => "honua_rt_" + family;

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Up to a quarter either side, so tasks that lost the database together do not reconnect together.
#pragma warning disable CA5394 // Jitter, not security.
    private static TimeSpan Jitter(TimeSpan delay)
        => TimeSpan.FromTicks((long)(delay.Ticks * (0.75 + (Random.Shared.NextDouble() * 0.5))));
#pragma warning restore CA5394

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The realtime listener is disabled because the database host is a connection proxy; realtime runs on the degraded poll. Set ConnectionStrings:RealtimeListener to the instance endpoint.")]
    private static partial void LogProxyHost(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The realtime listener is disabled because its connection string could not be parsed; realtime runs on the degraded poll. ConnectionStrings:RealtimeListener must be a plain connection string.")]
    private static partial void LogUnusableConnectionString(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The realtime listener lost its connection; reconnecting in about {Delay}. Realtime runs on the degraded poll meanwhile.")]
    private static partial void LogDisconnected(ILogger logger, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The realtime listener reconnected.")]
    private static partial void LogReconnected(ILogger logger);
}
