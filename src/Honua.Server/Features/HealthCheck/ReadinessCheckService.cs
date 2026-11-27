// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.FileStorage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Honua.Server.Features.HealthCheck;

internal sealed class ReadinessCheckService : IReadinessCheckService
{
    // The inline recovery attempt stays inside the API Gateway integration timeout.
    internal static readonly TimeSpan InlineRecoveryBudget = TimeSpan.FromSeconds(10);

    private readonly IDatabaseHealthChecker _databaseHealthChecker;
    private readonly ICacheHealthChecker? _cacheHealthChecker;
    private readonly IFeatureChangeEventStoreHealth? _featureChangeEventStoreHealth;
    private readonly IAlertDispatchHealth? _alertDispatchHealth;
    private readonly IAlertEvaluationHealth? _alertEvaluationHealth;
    private readonly AlertOptions _alertOptions;
    private readonly GeoprocessingOutputStoreHealthCheck? _outputStoreHealth;
    private readonly MigrationState _migrationState;
    private readonly DegradedStartupContext? _degradedStartup;
    private readonly ILogger<ReadinessCheckService> _logger;
    private ReadinessProbeCoalescer _coalescer = new();

    public ReadinessCheckService(
        IDatabaseHealthChecker databaseHealthChecker,
        MigrationState migrationState,
        ILogger<ReadinessCheckService> logger,
        ICacheHealthChecker? cacheHealthChecker = null,
        IFeatureChangeEventStoreHealth? featureChangeEventStoreHealth = null,
        IAlertDispatchHealth? alertDispatchHealth = null,
        IAlertEvaluationHealth? alertEvaluationHealth = null,
        IOptions<AlertOptions>? alertOptions = null,
        GeoprocessingOutputStoreHealthCheck? outputStoreHealth = null,
        DegradedStartupContext? degradedStartup = null)
    {
        _degradedStartup = degradedStartup;
        _databaseHealthChecker = databaseHealthChecker;
        _cacheHealthChecker = cacheHealthChecker;
        _featureChangeEventStoreHealth = featureChangeEventStoreHealth;
        _alertDispatchHealth = alertDispatchHealth;
        _alertEvaluationHealth = alertEvaluationHealth;
        _alertOptions = alertOptions?.Value ?? new AlertOptions();
        _outputStoreHealth = outputStoreHealth;
        _migrationState = migrationState ?? throw new ArgumentNullException(nameof(migrationState));
        _logger = logger;
    }

    internal TimeProvider TimeProvider
    {
        init => _coalescer = new ReadinessProbeCoalescer(value);
    }

    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (await EvaluateLocalPreChecksAsync(cancellationToken) is { } preCheckFailure)
        {
            return preCheckFailure;
        }

        var dependencyResult = await _coalescer.ExecuteAsync(EvaluateDependencyProbeAsync, cancellationToken);
        if (!dependencyResult.IsReady)
        {
            return dependencyResult;
        }

        return await EvaluateLocalPostChecksAsync(cancellationToken);
    }

    internal async Task<ReadinessResult?> EvaluateLocalPreChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            // On AWS Lambda the background recovery loop only runs while an invocation executes,
            // so run one bounded recovery attempt on degraded probes (honua-release run 40746787170).
            if (_degradedStartup is { IsAwaitingDatabase: true })
            {
                await _degradedStartup.TryRecoverOnReadinessProbeAsync(InlineRecoveryBudget, cancellationToken);
            }

            if (_degradedStartup is { IsAwaitingDatabase: true } degraded)
            {
                return BuildDegradedResult(degraded);
            }

            return CheckMigrationReadiness();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional catch-all: readiness pre-check boundary maps unexpected errors to NotReady.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HandleReadinessFailure("Database", ReadinessReasonCodes.DatabaseUnavailable, ex);
        }
    }

    internal async Task<ReadinessResult> EvaluateDependencyProbeAsync(CancellationToken cancellationToken)
    {
        var currentCheckName = "Database";
        var currentReasonCode = ReadinessReasonCodes.DatabaseUnavailable;
        try
        {
            if (await CheckDatabaseHealthAsync(cancellationToken) is { } dbFailure)
            {
                return dbFailure;
            }

            if (_cacheHealthChecker is null)
            {
                return ReadinessResult.Ready();
            }

            currentCheckName = "Cache";
            currentReasonCode = ReadinessReasonCodes.CacheUnavailable;
            return await CheckCacheHealthAsync(_cacheHealthChecker, cancellationToken)
                ?? ReadinessResult.Ready();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional catch-all: readiness dependency boundary maps unexpected errors to NotReady.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HandleReadinessFailure(currentCheckName, currentReasonCode, ex);
        }
    }

    internal async Task<ReadinessResult> EvaluateLocalPostChecksAsync(CancellationToken cancellationToken)
    {
        var currentCheckName = "Feature-change event storage";
        var currentReasonCode = ReadinessReasonCodes.FeatureChangeEventStorageUnavailable;
        try
        {
            if (CheckFeatureChangeEventStore() is { } storeFailure)
            {
                return storeFailure;
            }

            currentCheckName = "Alert control plane";
            currentReasonCode = ReadinessReasonCodes.AlertDispatchStalled;
            if (CheckAlertControlLoops() is { } alertFailure)
            {
                return alertFailure;
            }

            currentCheckName = "Referenced output store";
            currentReasonCode = ReadinessReasonCodes.GeoprocessingOutputStoreAttestationUnavailable;
            return await CheckOutputStoreHealthAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentional catch-all: readiness post-check boundary maps unexpected errors to NotReady.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return HandleReadinessFailure(currentCheckName, currentReasonCode, ex);
        }
    }

    private ReadinessResult? CheckAlertControlLoops()
    {
        var now = DateTimeOffset.UtcNow;
        if (IsAlertDispatchStalled(now, out var dispatchReason))
        {
            Log.HealthCheckExecuted(_logger, "AlertDispatch", "Unhealthy", 0);
            return ReadinessResult.NotReady(dispatchReason, ReadinessReasonCodes.AlertDispatchStalled);
        }

        if (IsAlertEvaluationStalled(now, out var evaluationReason))
        {
            Log.HealthCheckExecuted(_logger, "AlertEvaluation", "Unhealthy", 0);
            return ReadinessResult.NotReady(evaluationReason, ReadinessReasonCodes.AlertEvaluationStalled);
        }

        return null;
    }

    private static ReadinessResult BuildDegradedResult(DegradedStartupContext degraded)
    {
        var schemaFloor = string.Equals(
            degraded.ReasonCode, ReadinessReasonCodes.SchemaFloor, StringComparison.Ordinal);
        var condition = schemaFloor
            ? "Database schema is behind this build"
            : "Database unavailable at startup";
        var waiting = schemaFloor
            ? "re-checking until the migrations are applied"
            : "retrying in the background";
        var reason = degraded.RecoveryFailure is { } stopped
            ? $"{condition} ({degraded.Reason ?? "unknown"}); background recovery stopped: {stopped}"
            : $"{condition} ({degraded.Reason ?? "unknown"}); {waiting}";
        return ReadinessResult.NotReady(
            reason,
            schemaFloor ? ReadinessReasonCodes.SchemaFloor : ReadinessReasonCodes.DatabaseStartupDegraded);
    }

    private ReadinessResult? CheckMigrationReadiness()
    {
        if (_migrationState.IsFailed)
        {
            return ReadinessResult.NotReady("Database migrations failed", ReadinessReasonCodes.MigrationsFailed);
        }

        if (_migrationState.IsRunning)
        {
            return ReadinessResult.NotReady("Database migrations in progress", ReadinessReasonCodes.MigrationsInProgress);
        }

        if (!_migrationState.IsReady)
        {
            return ReadinessResult.NotReady("Database migrations not completed", ReadinessReasonCodes.MigrationsNotCompleted);
        }

        return null;
    }

    private async Task<ReadinessResult?> CheckDatabaseHealthAsync(CancellationToken cancellationToken)
    {
        var databaseStopwatch = Stopwatch.StartNew();
        bool isDatabaseHealthy = await _databaseHealthChecker.IsDatabaseHealthyAsync(cancellationToken);
        databaseStopwatch.Stop();

        if (!isDatabaseHealthy)
        {
            Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Unhealthy", databaseStopwatch.Elapsed.TotalMilliseconds);
            return ReadinessResult.NotReady("Database unavailable", ReadinessReasonCodes.DatabaseUnavailable);
        }

        Log.HealthCheckExecuted(_logger, "DatabaseHealth", "Healthy", databaseStopwatch.Elapsed.TotalMilliseconds);
        return null;
    }

    private async Task<ReadinessResult?> CheckCacheHealthAsync(
        ICacheHealthChecker cacheHealthChecker,
        CancellationToken cancellationToken)
    {
        var cacheStopwatch = Stopwatch.StartNew();
        bool isCacheHealthy = await cacheHealthChecker.IsCacheHealthyAsync(cancellationToken);
        cacheStopwatch.Stop();
        string cacheStatus = isCacheHealthy
            ? (cacheHealthChecker.IsUsingFallback ? "Healthy (fallback)" : "Healthy")
            : "Unhealthy";
        Log.HealthCheckExecuted(_logger, "CacheHealth", cacheStatus, cacheStopwatch.Elapsed.TotalMilliseconds);

        return isCacheHealthy
            ? null
            : ReadinessResult.NotReady("Cache unavailable", ReadinessReasonCodes.CacheUnavailable);
    }

    private ReadinessResult? CheckFeatureChangeEventStore()
    {
        if (_featureChangeEventStoreHealth is null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        var canPersistEvents = _featureChangeEventStoreHealth.CanPersistEvents;
        stopwatch.Stop();

        if (!canPersistEvents)
        {
            Log.HealthCheckExecuted(_logger, "FeatureChangeEventStore", "Unhealthy", stopwatch.Elapsed.TotalMilliseconds);
            return ReadinessResult.NotReady(
                "Feature-change event storage unavailable",
                ReadinessReasonCodes.FeatureChangeEventStorageUnavailable);
        }

        var status = _featureChangeEventStoreHealth.IsUsingInMemoryFallback ? "Healthy (fallback)" : "Healthy";
        Log.HealthCheckExecuted(_logger, "FeatureChangeEventStore", status, stopwatch.Elapsed.TotalMilliseconds);
        return null;
    }

    private async Task<ReadinessResult> CheckOutputStoreHealthAsync(CancellationToken cancellationToken)
    {
        if (_outputStoreHealth is null)
        {
            return ReadinessResult.Ready();
        }

        var outputHealth = await _outputStoreHealth.CheckHealthAsync(new HealthCheckContext(), cancellationToken);
        if (outputHealth.Status != HealthStatus.Healthy)
        {
            return ReadinessResult.NotReady(
                "Referenced output store attestation unavailable",
                ReadinessReasonCodes.GeoprocessingOutputStoreAttestationUnavailable);
        }

        return ReadinessResult.Ready();
    }

    private ReadinessResult HandleReadinessFailure(string checkName, string reasonCode, Exception ex)
    {
        var failureMessage = $"{checkName} health check failed";
        Log.DatabaseConnectionFailed(_logger, $"{failureMessage}: {ex.Message}", ex);
        return ReadinessResult.NotReady(failureMessage, reasonCode, ex);
    }

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

    // Only a fleet that cannot acquire the lease, or a leader with a stale heartbeat, trips this check; a healthy follower does not.
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

// Singleton DI entry point that keeps fast local checks (startup recovery, migrations,
// alert heartbeats, output-store attestation) live on every call while coalescing and
// briefly caching the database/cache dependency probe so unauthenticated bursts cannot
// exhaust the PostGIS connection pool.
internal sealed class CoalescingReadinessCheckService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IReadinessCheckService
{
    private readonly ReadinessProbeCoalescer _coalescer = new(timeProvider);

    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredService<ReadinessCheckService>();
        if (await inner.EvaluateLocalPreChecksAsync(cancellationToken) is { } preCheckFailure)
        {
            return preCheckFailure;
        }

        var dependencyResult = await _coalescer.ExecuteAsync(RunScopedDependencyProbeAsync, cancellationToken);
        if (!dependencyResult.IsReady)
        {
            return dependencyResult;
        }

        return await inner.EvaluateLocalPostChecksAsync(cancellationToken);
    }

    private async Task<ReadinessResult> RunScopedDependencyProbeAsync(CancellationToken cancellationToken)
    {
        await using var probeScope = scopeFactory.CreateAsyncScope();
        var inner = probeScope.ServiceProvider.GetRequiredService<ReadinessCheckService>();
        return await inner.EvaluateDependencyProbeAsync(cancellationToken);
    }
}

// Coalesces concurrent readiness probes into a single dependency check and caches the
// outcome briefly so bursts cannot exhaust the database connection pool.
internal sealed class ReadinessProbeCoalescer
{
    internal static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly Lock _gate = new();
    private Task<ReadinessResult>? _inFlight;
    private ReadinessResult? _cachedResult;
    private long _cachedAtTimestamp;

    internal ReadinessProbeCoalescer(TimeProvider? timeProvider = null, TimeSpan? cacheTtl = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    public Task<ReadinessResult> ExecuteAsync(
        Func<CancellationToken, Task<ReadinessResult>> probe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sharedTask = GetOrStartProbe(probe);
        return sharedTask.WaitAsync(cancellationToken);
    }

    private Task<ReadinessResult> GetOrStartProbe(
        Func<CancellationToken, Task<ReadinessResult>> probe)
    {
        lock (_gate)
        {
            if (_cachedResult is { } cached &&
                _timeProvider.GetElapsedTime(_cachedAtTimestamp) < _cacheTtl)
            {
                return Task.FromResult(cached);
            }

            if (_inFlight is not { IsCompleted: false })
            {
                _inFlight = StartProbe(probe);
            }

            return _inFlight;
        }
    }

    private Task<ReadinessResult> StartProbe(
        Func<CancellationToken, Task<ReadinessResult>> probe)
    {
        // The shared check must not carry the first caller's trace, log scope or tenant context.
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => RunProbeAsync(probe));
        }
    }

    private async Task<ReadinessResult> RunProbeAsync(
        Func<CancellationToken, Task<ReadinessResult>> probe)
    {
        using var deadline = new CancellationTokenSource(ProbeTimeout);
        try
        {
            var result = await probe(deadline.Token);
            CacheResult(result);
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return ReadinessResult.NotReady("Readiness check timed out", ReadinessReasonCodes.DatabaseUnavailable);
        }
    }

    private void CacheResult(ReadinessResult result)
    {
        lock (_gate)
        {
            _cachedResult = result;
            _cachedAtTimestamp = _timeProvider.GetTimestamp();
        }
    }
}
