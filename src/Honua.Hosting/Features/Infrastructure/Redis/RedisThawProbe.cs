// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Honua.Infrastructure.Redis;

/// <summary>
/// Re-validates every Redis connection before the first request after an idle gap, so a torn
/// pipe fails a probe instead of the request (honua-server#6753).
/// </summary>
/// <remarks>
/// AWS Lambda freezes the sandbox between invocations. After a long freeze the first Redis
/// command on a StackExchange.Redis multiplexer can find its socket dead; the client tears the
/// connection down and every command in flight at that moment fails, most of them with the
/// completed-reader <see cref="InvalidOperationException"/> (<see cref="RedisClientFault"/>).
/// One read on each connection, retried once, absorbs that teardown: the retry waits in the
/// multiplexer backlog for the reconnect (<c>AbortOnConnectFail</c> is false), so the request
/// that follows runs on a live connection. This covers every Redis read path at once, including
/// the ones that latch a local failure state on a single fault. A probe never fails the request:
/// a connection that is still down is left to the caller's own error handling.
/// </remarks>
internal sealed partial class RedisThawProbe
{
    /// <summary>Idle time after which the next request re-validates the connections.</summary>
    internal static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromSeconds(5);

    /// <summary>Key read through the distributed cache; it is never written.</summary>
    internal const string CacheProbeKey = "redis-thaw-probe";

    private const int MaxAttempts = 2;

    private readonly IReadOnlyList<ProbeTarget> _targets;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleThreshold;
    private readonly ILogger _logger;
    private long _lastActivityTimestamp;
    private long _lastActivityUtcTicks;

    internal RedisThawProbe(
        IReadOnlyList<ProbeTarget> targets,
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan? idleThreshold = null)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idleThreshold = idleThreshold ?? DefaultIdleThreshold;
        _lastActivityTimestamp = _timeProvider.GetTimestamp();
        _lastActivityUtcTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    /// <summary>A named Redis read that proves one connection is live.</summary>
    /// <param name="Name">Connection name for logs.</param>
    /// <param name="Probe">The read.</param>
    internal sealed record ProbeTarget(string Name, Func<CancellationToken, Task> Probe);

    /// <summary>Whether there is any Redis connection to probe.</summary>
    internal bool HasTargets => _targets.Count > 0;

    /// <summary>
    /// Builds the probe over the infrastructure multiplexer and the Redis distributed cache,
    /// whichever are registered. The cache keeps its own multiplexer, so it is probed through
    /// <see cref="IDistributedCache"/>.
    /// </summary>
    /// <param name="services">Root service provider.</param>
    /// <returns>The probe.</returns>
    internal static RedisThawProbe Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var targets = new List<ProbeTarget>(2);
        if (services.GetService<IConnectionMultiplexer>() is { } multiplexer)
        {
            targets.Add(new ProbeTarget(
                "infrastructure",
                async _ => await multiplexer.GetDatabase().PingAsync().ConfigureAwait(false)));
        }

        if (services.GetService<IDistributedCache>() is RedisCache cache)
        {
            targets.Add(new ProbeTarget(
                "distributed-cache",
                async cancellationToken => await cache.GetAsync(CacheProbeKey, cancellationToken).ConfigureAwait(false)));
        }

        return new RedisThawProbe(
            targets,
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            services.GetRequiredService<ILoggerFactory>().CreateLogger<RedisThawProbe>());
    }

    /// <summary>Records request activity; the idle gap is measured from the last call.</summary>
    internal void MarkActive()
    {
        Interlocked.Exchange(ref _lastActivityTimestamp, _timeProvider.GetTimestamp());
        Interlocked.Exchange(ref _lastActivityUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
    }

    /// <summary>
    /// Probes every connection when the host was idle for at least the threshold, then marks
    /// the host active. Only the request that observes the gap probes.
    /// </summary>
    /// <param name="cancellationToken">The request's abort token.</param>
    /// <returns><see langword="true"/> when a probe ran.</returns>
    internal async Task<bool> ProbeIfIdleAsync(CancellationToken cancellationToken)
    {
        var nowTimestamp = _timeProvider.GetTimestamp();
        var nowUtcTicks = _timeProvider.GetUtcNow().UtcTicks;
        var previousTimestamp = Interlocked.Exchange(ref _lastActivityTimestamp, nowTimestamp);
        var previousUtcTicks = Interlocked.Exchange(ref _lastActivityUtcTicks, nowUtcTicks);

        // A frozen microVM may stop the monotonic clock, so take the larger of both clocks.
        var monotonicIdle = _timeProvider.GetElapsedTime(previousTimestamp, nowTimestamp);
        var wallClockIdle = TimeSpan.FromTicks(nowUtcTicks - previousUtcTicks);
        var idle = monotonicIdle > wallClockIdle ? monotonicIdle : wallClockIdle;
        if (idle < _idleThreshold || _targets.Count == 0)
        {
            return false;
        }

        foreach (var target in _targets)
        {
            await ProbeAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task ProbeAsync(ProbeTarget target, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await target.Probe(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && RedisClientFault.IsRedisTransportFault(ex))
            {
                // The expected thaw outcome: the dead connection was torn down under this
                // read. The retry waits for the reconnect.
                LogProbeRetry(_logger, target.Name, ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Still down, or an unexpected failure: the request's own Redis calls decide.
                LogProbeFailed(_logger, target.Name, ex);
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Redis {Connection} connection failed its post-idle probe; retrying once after reconnect")]
    private static partial void LogProbeRetry(ILogger logger, string connection, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Redis {Connection} connection is still unavailable after its post-idle probe")]
    private static partial void LogProbeFailed(ILogger logger, string connection, Exception exception);
}

/// <summary>
/// Runs <see cref="RedisThawProbe"/> ahead of the rest of the pipeline and marks the host active
/// once the request finishes, so the idle gap is the time the sandbox could have been frozen.
/// </summary>
internal sealed class RedisThawProbeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RedisThawProbe _probe;

    public RedisThawProbeMiddleware(RequestDelegate next, RedisThawProbe probe)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _probe.ProbeIfIdleAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            _probe.MarkActive();
        }
    }
}

/// <summary>Registration for <see cref="RedisThawProbe"/>.</summary>
internal static class RedisThawProbeExtensions
{
    /// <summary>Registers the probe. Call only on hosts that are frozen between requests.</summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The service collection.</returns>
    internal static IServiceCollection AddRedisThawProbe(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(RedisThawProbe.Create);
        return services;
    }

    /// <summary>
    /// Adds <see cref="RedisThawProbeMiddleware"/> when the probe is registered and there is a
    /// Redis connection to probe; otherwise leaves the pipeline untouched.
    /// </summary>
    /// <param name="app">Application builder.</param>
    /// <returns>The application builder.</returns>
    internal static IApplicationBuilder UseRedisThawProbe(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.ApplicationServices.GetService<RedisThawProbe>() is { HasTargets: true }
            ? app.UseMiddleware<RedisThawProbeMiddleware>()
            : app;
    }
}
