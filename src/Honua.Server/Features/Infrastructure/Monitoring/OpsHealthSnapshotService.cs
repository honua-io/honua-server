// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Composes the consolidated admin ops-health snapshot from the shared health/monitoring seams.
/// Introduced so the endpoint handler stays a thin adapter (one dependency) while this service holds
/// the several collaborators the snapshot fuses.
/// </summary>
internal interface IOpsHealthSnapshotService
{
    /// <summary>
    /// Builds the consolidated ops-health snapshot.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The composed ops-health snapshot response.</returns>
    Task<OpsHealthSnapshotResponse> GetAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class OpsHealthSnapshotService : IOpsHealthSnapshotService
{
    private readonly HealthCheckService _healthCheckService;
    private readonly IDeployPreflightProbe _deployProbe;
    private readonly IOptionsMonitor<ControlPlaneOptions> _controlPlaneOptions;
    private readonly IAlertDispatchHealth _alertHealth;
    private readonly ProductionMetricsCollector _metricsCollector;
    private readonly IExecutionJobStore? _jobStore;

    public OpsHealthSnapshotService(
        HealthCheckService healthCheckService,
        IDeployPreflightProbe deployProbe,
        IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
        IAlertDispatchHealth alertHealth,
        ProductionMetricsCollector metricsCollector,
        IExecutionJobStore? jobStore = null)
    {
        _healthCheckService = healthCheckService;
        _deployProbe = deployProbe;
        _controlPlaneOptions = controlPlaneOptions;
        _alertHealth = alertHealth;
        _metricsCollector = metricsCollector;
        _jobStore = jobStore;
    }

    public async Task<OpsHealthSnapshotResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var healthReport = await _healthCheckService.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var deploySnapshot = await _deployProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var gpQueue = await BuildGpQueueViewAsync(cancellationToken).ConfigureAwait(false);
        var healthMetrics = _metricsCollector.GetHealthMetrics();

        return new OpsHealthSnapshotResponse
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            OverallStatus = healthReport.Status.ToString(),
            Health = BuildHealthView(healthReport),
            ServingLatency = BuildServingLatencyView(),
            Geoprocessing = gpQueue,
            AlertDispatch = BuildAlertDispatchView(),
            Deploy = BuildDeployView(deploySnapshot),
            Database = new OpsDatabaseView
            {
                ConnectionPoolUtilization = healthMetrics.HasDatabaseConnectionPoolUtilization
                    ? healthMetrics.DatabaseConnectionPoolUtilization
                    : null,
                HasConnectionPoolData = healthMetrics.HasDatabaseConnectionPoolUtilization,
                ActiveConnections = healthMetrics.ActiveConnections,
                ConnectionAcquisitionTimeouts = healthMetrics.ConnectionAcquisitionTimeouts,
                ConnectionAcquisitionFailures = healthMetrics.ConnectionAcquisitionFailures,
                CacheHitRatio = healthMetrics.CacheHitRatio,
                ErrorRate = healthMetrics.ErrorRate,
            },
        };
    }

    private static OpsHealthChecksView BuildHealthView(HealthReport report)
    {
        var entries = report.Entries
            .Select(entry => new OpsHealthCheckEntryView
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description,
                DurationMs = entry.Value.Duration.TotalMilliseconds,
            })
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();

        return new OpsHealthChecksView
        {
            Status = report.Status.ToString(),
            TotalDurationMs = report.TotalDuration.TotalMilliseconds,
            Entries = entries,
        };
    }

    private static OpsServingLatencyView BuildServingLatencyView()
    {
        var snapshot = HonuaTelemetry.GetServingLatencySnapshot();
        var protocols = snapshot.Protocols
            .Select(protocol => new OpsServingLatencyProtocolView
            {
                Protocol = protocol.Protocol,
                RequestCount = protocol.RequestCount,
                ErrorCount = protocol.ErrorCount,
                ErrorRate = protocol.ErrorRate,
                P50Ms = protocol.P50Ms,
                P95Ms = protocol.P95Ms,
                P99Ms = protocol.P99Ms,
                MaxMs = protocol.MaxMs,
            })
            .ToList();

        return new OpsServingLatencyView
        {
            WindowSeconds = snapshot.WindowSeconds,
            Protocols = protocols,
        };
    }

    private async Task<OpsGpQueueView> BuildGpQueueViewAsync(CancellationToken cancellationToken)
    {
        if (_jobStore is null)
        {
            return new OpsGpQueueView { TotalActive = 0, Available = false, Buckets = [] };
        }

        var activeJobs = await _jobStore.ListActiveAsync(kind: null, cancellationToken).ConfigureAwait(false);
        var queueDepth = ControlPlaneTelemetry.ComputeQueueDepth(activeJobs);
        var buckets = queueDepth
            .Select(entry => new OpsGpQueueBucketView
            {
                Status = entry.Status,
                Backend = entry.Backend,
                Count = entry.Count,
            })
            .OrderBy(bucket => bucket.Status, StringComparer.Ordinal)
            .ThenBy(bucket => bucket.Backend, StringComparer.Ordinal)
            .ToList();

        return new OpsGpQueueView
        {
            TotalActive = buckets.Sum(bucket => bucket.Count),
            Available = true,
            Buckets = buckets,
        };
    }

    private OpsAlertDispatchView BuildAlertDispatchView()
    {
        var backlog = _alertHealth.LastBacklog;
        return new OpsAlertDispatchView
        {
            DispatcherRunning = _alertHealth.IsDispatcherRunning,
            DispatcherEnabled = _alertHealth.IsDispatcherEnabled,
            StoragePollFailing = _alertHealth.IsStoragePollFailing,
            LastPollAt = _alertHealth.LastPollAt,
            PendingCount = backlog?.PendingCount,
            DeadLetteredCount = backlog?.DeadLetteredCount,
        };
    }

    private OpsDeployReadinessView BuildDeployView(DeployPreflightSnapshot snapshot)
    {
        var skew = PlatformReleaseSkewProjector.Build(_controlPlaneOptions.CurrentValue);
        return new OpsDeployReadinessView
        {
            Status = snapshot.Status,
            ReadyForCoordinatedDeploy = snapshot.ReadyForCoordinatedDeploy,
            PendingMigrationsCount = snapshot.Migration.PendingScripts.Count,
            PendingContractScriptsCount = snapshot.Migration.PendingContractScripts.Count,
            PlatformRelease = new OpsPlatformReleaseView
            {
                ReleaseVersion = skew.ReleaseVersion,
                ReleaseDeclared = skew.ReleaseDeclared,
                IsCoVersioned = skew.IsCoVersioned,
                SkewedIds = skew.SkewedIds,
            },
        };
    }
}
