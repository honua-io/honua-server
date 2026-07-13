// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Service for orchestrating readiness health checks
/// </summary>
internal sealed class ReadinessCheckService : IReadinessCheckService
{
    private readonly IDatabaseHealthChecker _databaseHealthChecker;
    private readonly ICacheHealthChecker? _cacheHealthChecker;
    private readonly IFeatureChangeEventStoreHealth? _featureChangeEventStoreHealth;
    private readonly IAlertDispatchHealth? _alertDispatchHealth;
    private readonly IAlertEvaluationHealth? _alertEvaluationHealth;
    private readonly AlertOptions _alertOptions;
    private readonly MigrationState _migrationState;
    private readonly ILogger<ReadinessCheckService> _logger;

    public ReadinessCheckService(
        IDatabaseHealthChecker databaseHealthChecker,
        MigrationState migrationState,
        ILogger<ReadinessCheckService> logger,
        ICacheHealthChecker? cacheHealthChecker = null,
        IFeatureChangeEventStoreHealth? featureChangeEventStoreHealth = null,
        IAlertDispatchHealth? alertDispatchHealth = null,
        IAlertEvaluationHealth? alertEvaluationHealth = null,
        IOptions<AlertOptions>? alertOptions = null)
    {
        _databaseHealthChecker = databaseHealthChecker;
        _cacheHealthChecker = cacheHealthChecker;
        _featureChangeEventStoreHealth = featureChangeEventStoreHealth;
        _alertDispatchHealth = alertDispatchHealth;
        _alertEvaluationHealth = alertEvaluationHealth;
        _alertOptions = alertOptions?.Value ?? new AlertOptions();
        _migrationState = migrationState ?? throw new ArgumentNullException(nameof(migrationState));
        _logger = logger;
    }

    /// <summary>
    /// Performs all readiness checks and returns the overall result
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Readiness check result</returns>
    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        var currentCheckName = "Database";
        try
        {
            if (_migrationState.IsFailed)
            {
                return ReadinessResult.NotReady("Database migrations failed");
            }

            if (_migrationState.IsRunning)
            {
                return ReadinessResult.NotReady("Database migrations in progress");
            }

            if (!_migrationState.IsReady)
            {
                return ReadinessResult.NotReady("Database migrations not completed");
            }

            // Check database health
            var databaseStopwatch = Stopwatch.StartNew();
            bool isDatabaseHealthy = await _databaseHealthChecker.IsDatabaseHealthyAsync(cancellationToken);
            databaseStopwatch.Stop();

            if (!isDatabaseHealthy)
            {
                // Log unhealthy database without exception
                Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Unhealthy", databaseStopwatch.Elapsed.TotalMilliseconds);
                return ReadinessResult.NotReady("Database unavailable");
            }

            Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Healthy", databaseStopwatch.Elapsed.TotalMilliseconds);

            // Check cache health (optional - cache unavailability doesn't make system not ready)
            if (_cacheHealthChecker != null)
            {
                currentCheckName = "Cache";
                var cacheStopwatch = Stopwatch.StartNew();
                bool isCacheHealthy = await _cacheHealthChecker.IsCacheHealthyAsync(cancellationToken);
                cacheStopwatch.Stop();
                string cacheStatus = isCacheHealthy
                    ? (_cacheHealthChecker.IsUsingFallback ? "Healthy (fallback)" : "Healthy")
                    : "Unhealthy";
                Log.HealthCheckExecuted(_logger, "CacheHealth", cacheStatus, cacheStopwatch.Elapsed.TotalMilliseconds);

                if (!isCacheHealthy)
                {
                    return ReadinessResult.NotReady("Cache unavailable");
                }
            }

            if (_featureChangeEventStoreHealth is not null)
            {
                currentCheckName = "Feature-change event storage";
                var featureChangeStoreStopwatch = Stopwatch.StartNew();
                var canPersistEvents = _featureChangeEventStoreHealth.CanPersistEvents;
                featureChangeStoreStopwatch.Stop();

                if (!canPersistEvents)
                {
                    Log.HealthCheckExecuted(
                        _logger,
                        "FeatureChangeEventStore",
                        "Unhealthy",
                        featureChangeStoreStopwatch.Elapsed.TotalMilliseconds);
                    return ReadinessResult.NotReady("Feature-change event storage unavailable");
                }

                // In-memory single-node mode (no Redis configured) is healthy-but-degraded,
                // mirroring the cache fallback check above; it must not fail readiness (#1618).
                Log.HealthCheckExecuted(
                    _logger,
                    "FeatureChangeEventStore",
                    _featureChangeEventStoreHealth.IsUsingInMemoryFallback ? "Healthy (fallback)" : "Healthy",
                    featureChangeStoreStopwatch.Elapsed.TotalMilliseconds);
            }

            // Alert control loops (#2810): operators page on readiness, so a stalled alert control
            // plane must surface here. Only *loop-broken* conditions fail readiness — a hung
            // dispatcher heartbeat, a hung evaluation leader, or a fleet-wide no-leader stall — never
            // a mere delivery backlog / dead-letter accumulation (those remain on the health-check
            // roll-up so they page without depooling the node). Fresh-start states (no heartbeat yet,
            // a healthy follower) never trip this.
            var now = DateTimeOffset.UtcNow;

            currentCheckName = "Alert dispatch";
            if (IsAlertDispatchStalled(now, out var dispatchReason))
            {
                Log.HealthCheckExecuted(_logger, "AlertDispatch", "Unhealthy", 0);
                return ReadinessResult.NotReady(dispatchReason);
            }

            currentCheckName = "Alert evaluation";
            if (IsAlertEvaluationStalled(now, out var evaluationReason))
            {
                Log.HealthCheckExecuted(_logger, "AlertEvaluation", "Unhealthy", 0);
                return ReadinessResult.NotReady(evaluationReason);
            }

            return ReadinessResult.Ready();
        }
        catch (OperationCanceledException)
        {
            // Re-throw cancellation exceptions to properly propagate cancellation
            throw;
        }
        catch (Exception ex)
        {
            // Log the error for debugging but don't expose details in response
            var failureMessage = $"{currentCheckName} health check failed";
            Log.DatabaseConnectionFailed(_logger, $"{failureMessage}: {ex.Message}", ex);
            return ReadinessResult.NotReady(failureMessage, ex);
        }
    }

    /// <summary>
    /// True when the alert dispatcher is enabled and its poll heartbeat has aged past the staleness
    /// threshold (the loop is wedged while still reporting "running"). A dispatcher that has not yet
    /// polled (fresh start, <c>LastPollAt</c> null) is not stalled.
    /// </summary>
    private bool IsAlertDispatchStalled(DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        if (_alertDispatchHealth is null || !_alertDispatchHealth.IsDispatcherEnabled)
        {
            return false;
        }

        if (_alertDispatchHealth.LastPollAt is { } lastPollAt
            && now - lastPollAt >= _alertOptions.Dispatch.HeartbeatStalenessThreshold)
        {
            reason = "Alert dispatcher heartbeat is stale (dispatch loop appears hung)";
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the alert evaluation loop is enabled and running but either no node can acquire the
    /// evaluation lease (a fleet-wide no-leader stall) or this node holds leadership with a stale
    /// productive-pass heartbeat (a hung leader). A healthy follower and fresh-start states do not
    /// trip this. Not-running is intentionally left to the health-check roll-up to avoid a
    /// depool during the brief startup window before the loop begins.
    /// </summary>
    private bool IsAlertEvaluationStalled(DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        if (_alertEvaluationHealth is null
            || !_alertEvaluationHealth.IsEvaluatorEnabled
            || !_alertEvaluationHealth.IsEvaluatorRunning)
        {
            return false;
        }

        if (_alertEvaluationHealth.LeaderAcquisitionFailingSince is { } failingSince
            && now - failingSince >= _alertOptions.Evaluation.NoLeaderThreshold)
        {
            reason = "No node can acquire the alert-evaluation lease (evaluation stalled fleet-wide, no leader)";
            return true;
        }

        if (_alertEvaluationHealth.IsLeader
            && _alertEvaluationHealth.LastLeaderPassAt is { } lastPass
            && now - lastPass >= _alertOptions.Evaluation.HeartbeatStalenessThreshold)
        {
            reason = "Alert-evaluation leader heartbeat is stale (evaluation loop appears hung)";
            return true;
        }

        return false;
    }
}
