// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.ControlPlane;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
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
    private readonly IOptions<OpsHealthRollupOptions> _rollupOptions;
    private readonly IExecutionJobStore? _jobStore;
    private readonly IOpsHealthRollupStore? _rollupStore;
    private readonly TimeProvider _timeProvider;

    public OpsHealthSnapshotService(
        HealthCheckService healthCheckService,
        IDeployPreflightProbe deployProbe,
        IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
        IAlertDispatchHealth alertHealth,
        ProductionMetricsCollector metricsCollector,
        IOptions<OpsHealthRollupOptions> rollupOptions,
        TimeProvider timeProvider,
        IExecutionJobStore? jobStore = null,
        IOpsHealthRollupStore? rollupStore = null)
    {
        _healthCheckService = healthCheckService;
        _deployProbe = deployProbe;
        _controlPlaneOptions = controlPlaneOptions;
        _alertHealth = alertHealth;
        _metricsCollector = metricsCollector;
        _rollupOptions = rollupOptions;
        _timeProvider = timeProvider;
        _jobStore = jobStore;
        _rollupStore = rollupStore;
    }

    public async Task<OpsHealthSnapshotResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var healthReport = await _healthCheckService.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var deploySnapshot = await _deployProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var gpQueue = await BuildGpQueueViewAsync(cancellationToken).ConfigureAwait(false);
        var healthMetrics = _metricsCollector.GetHealthMetrics();
        var servingLatencyResult = await BuildServingLatencyViewAsync(cancellationToken).ConfigureAwait(false);
        var servingLatency = servingLatencyResult.View;

        var generatedAt = _timeProvider.GetUtcNow();
        var alertDispatch = BuildAlertDispatchView();
        var database = new OpsDatabaseView
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
        };

        var sectionSources = new[]
        {
            EvidencePostureFactory.Complete(
                EvidencePostureVocabulary.SourceIds.OpsHealthChecks,
                EvidencePostureVocabulary.BackendKinds.InProcess,
                "aspnet-health-checks",
                generatedAt,
                SectionValidity),
            BuildServingLatencySource(servingLatencyResult, generatedAt),
            gpQueue.Available
                ? EvidencePostureFactory.Complete(
                    EvidencePostureVocabulary.SourceIds.GpQueue,
                    EvidencePostureVocabulary.BackendKinds.DurableStore,
                    "execution-job-store",
                    generatedAt,
                    SectionValidity)
                : EvidencePostureFactory.NotConfigured(
                    EvidencePostureVocabulary.SourceIds.GpQueue,
                    EvidencePostureVocabulary.BackendKinds.DurableStore,
                    "execution-job-store"),
            BuildAlertDispatchSource(alertDispatch),
            EvidencePostureFactory.Complete(
                EvidencePostureVocabulary.SourceIds.DeployRelease,
                EvidencePostureVocabulary.BackendKinds.ConfigProjection,
                "control-plane-options",
                generatedAt,
                ProjectionValidity),
            database.HasConnectionPoolData
                ? EvidencePostureFactory.Complete(
                    EvidencePostureVocabulary.SourceIds.DatabaseCache,
                    EvidencePostureVocabulary.BackendKinds.InProcess,
                    "production-metrics",
                    generatedAt,
                    SectionValidity)
                : EvidencePostureFactory.Unavailable(
                    EvidencePostureVocabulary.SourceIds.DatabaseCache,
                    EvidencePostureVocabulary.BackendKinds.InProcess,
                    "production-metrics",
                    EvidencePostureVocabulary.ReasonCodes.SourceUnavailable,
                    maximumAge: SectionValidity),
        };

        // Validate the sections first so the documented top-level `honua_ops_health` source can
        // summarize them truthfully: it is complete only when every section is individually
        // actionable, and it publishes which section ids it actually covered.
        var validatedSections = sectionSources
            .Select(source => EvidencePostureFactory.Validate(source, generatedAt))
            .ToArray();
        var aggregate = EvidencePostureFactory.Aggregate(
            EvidencePostureVocabulary.SourceIds.OpsHealth,
            "ops-health-composer",
            SectionValidity,
            validatedSections);

        return new OpsHealthSnapshotResponse
        {
            EvidencePosture = EvidencePostureFactory.Build(generatedAt, [aggregate, .. validatedSections]),
            GeneratedAt = generatedAt,
            OverallStatus = healthReport.Status.ToString(),
            Health = BuildHealthView(healthReport),
            ServingLatency = servingLatency,
            Geoprocessing = gpQueue,
            AlertDispatch = alertDispatch,
            Deploy = BuildDeployView(deploySnapshot),
            Database = database,
        };
    }

    /// <summary>
    /// Server-owned maximum observation age for the live in-process/durable sections. Clients read
    /// this instead of inferring freshness from the response timestamp.
    /// </summary>
    private static readonly TimeSpan SectionValidity = TimeSpan.FromMinutes(2);

    /// <summary>Server-owned maximum observation age for configuration-projected sections.</summary>
    private static readonly TimeSpan ProjectionValidity = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Projects the serving-latency section. A cluster rollup that failed to read leaves this
    /// replica reporting only its own samples, which is incomplete cluster coverage — never a
    /// whole-cluster "complete" posture.
    /// </summary>
    private static EvidenceSourceEnvelope BuildServingLatencySource(
        ServingLatencyReadResult result,
        DateTimeOffset generatedAt)
    {
        var coverage = new EvidenceSourceCoverage
        {
            ObservedReplicaCount = result.View.ClusterReplicaCount ?? 1,
            ExpectedReplicaCount = result.RollupDegraded ? null : result.View.ClusterReplicaCount ?? 1,
        };

        return result.RollupDegraded
            ? EvidencePostureFactory.Partial(
                EvidencePostureVocabulary.SourceIds.ServingLatency,
                EvidencePostureVocabulary.BackendKinds.InProcess,
                "honua-telemetry",
                generatedAt,
                SectionValidity,
                EvidencePostureVocabulary.ReasonCodes.IncompleteCoverage,
                coverage)
            : EvidencePostureFactory.Complete(
                EvidencePostureVocabulary.SourceIds.ServingLatency,
                EvidencePostureVocabulary.BackendKinds.InProcess,
                "honua-telemetry",
                generatedAt,
                SectionValidity,
                coverage);
    }

    /// <summary>
    /// Projects the alert-dispatch section. An enabled dispatcher that has never completed a poll
    /// has no heartbeat to report, so its observation time stays missing rather than being filled
    /// in with the response time.
    /// </summary>
    private static EvidenceSourceEnvelope BuildAlertDispatchSource(OpsAlertDispatchView alertDispatch)
    {
        if (!alertDispatch.DispatcherEnabled)
        {
            return EvidencePostureFactory.NotConfigured(
                EvidencePostureVocabulary.SourceIds.AlertDispatch,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                "alert-dispatch-store");
        }

        return alertDispatch.LastPollAt is { } lastPollAt
            ? EvidencePostureFactory.Complete(
                EvidencePostureVocabulary.SourceIds.AlertDispatch,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                "alert-dispatch-store",
                lastPollAt,
                SectionValidity)
            : EvidencePostureFactory.Unavailable(
                EvidencePostureVocabulary.SourceIds.AlertDispatch,
                EvidencePostureVocabulary.BackendKinds.DurableStore,
                "alert-dispatch-store",
                EvidencePostureVocabulary.ReasonCodes.NeverSucceeded,
                maximumAge: SectionValidity);
    }

    /// <summary>Serving-latency projection plus whether the cluster rollup read degraded.</summary>
    private readonly record struct ServingLatencyReadResult(OpsServingLatencyView View, bool RollupDegraded);

    private static OpsHealthChecksView BuildHealthView(HealthReport report)
    {
        var entries = report.Entries
            .Select(entry => new OpsHealthCheckEntryView
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description,
                DurationMs = entry.Value.Duration.TotalMilliseconds,
                OutputStoreAttestation = entry.Key == "gp-output-store"
                    ? entry.Value.Data.Where(item => item.Value is string)
                        .ToDictionary(item => item.Key, item => (string)item.Value, StringComparer.Ordinal)
                    : null,
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

    // Builds the serving-latency view. On a multi-node deployment the responding instance's live in-process
    // window is merged with peers' most-recent persisted rollup flushes so the snapshot reports whole-cluster
    // health, not just the instance that served the request (#2553). Fail-open: a rollup-store outage or a
    // missing store (non-Postgres) transparently degrades to the local-only view.
    private async Task<ServingLatencyReadResult> BuildServingLatencyViewAsync(CancellationToken cancellationToken)
    {
        var snapshot = HonuaTelemetry.GetServingLatencySnapshot();

        // Seed the per-protocol accumulator with this replica's live points.
        var local = new Dictionary<string, List<OpsHealthLatencyPoint>>(StringComparer.Ordinal);
        foreach (var protocol in snapshot.Protocols)
        {
            local[protocol.Protocol] = new List<OpsHealthLatencyPoint>
            {
                new()
                {
                    Protocol = protocol.Protocol,
                    RequestCount = protocol.RequestCount,
                    ErrorCount = protocol.ErrorCount,
                    P50Ms = protocol.P50Ms,
                    P95Ms = protocol.P95Ms,
                    P99Ms = protocol.P99Ms,
                    MaxMs = protocol.MaxMs,
                },
            };
        }

        var clusterReplicaCount = 1;
        var rollupDegraded = false;
        if (_rollupStore is not null)
        {
            try
            {
                var options = _rollupOptions.Value;
                var since = DateTimeOffset.UtcNow.AddSeconds(-options.LiveMergeRecencyWindowSeconds);
                var selfReplicaId = options.ResolveReplicaId();
                var recent = await _rollupStore.ReadRecentLatencyAsync(since, selfReplicaId, cancellationToken).ConfigureAwait(false);

                // Keep only the most recent bucket per (replica, protocol) so overlapping rolling-window
                // flushes are not double-counted.
                var latestPerReplicaProtocol = recent
                    .GroupBy(row => (row.ReplicaId, row.Point.Protocol))
                    .Select(group => group.OrderByDescending(row => row.BucketStart).First());

                var peerReplicas = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in latestPerReplicaProtocol)
                {
                    peerReplicas.Add(row.ReplicaId);
                    if (!local.TryGetValue(row.Point.Protocol, out var list))
                    {
                        list = new List<OpsHealthLatencyPoint>();
                        local[row.Point.Protocol] = list;
                    }

                    list.Add(row.Point);
                }

                clusterReplicaCount += peerReplicas.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail-open for the diagnostic view: report the responding instance only. The
                // evidence envelope still records that cluster coverage is incomplete.
                clusterReplicaCount = 1;
                rollupDegraded = true;
            }
        }

        var protocols = local
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var merged = OpsHealthRollupAggregation.MergeAcrossReplicas(pair.Key, pair.Value);
                return new OpsServingLatencyProtocolView
                {
                    Protocol = merged.Protocol,
                    RequestCount = merged.RequestCount,
                    ErrorCount = merged.ErrorCount,
                    ErrorRate = merged.ErrorRate,
                    P50Ms = merged.P50Ms,
                    P95Ms = merged.P95Ms,
                    P99Ms = merged.P99Ms,
                    MaxMs = merged.MaxMs,
                };
            })
            .ToList();

        return new ServingLatencyReadResult(
            new OpsServingLatencyView
            {
                WindowSeconds = snapshot.WindowSeconds,
                Protocols = protocols,
                ClusterReplicaCount = clusterReplicaCount,
            },
            rollupDegraded);
    }

    private async Task<OpsGpQueueView> BuildGpQueueViewAsync(CancellationToken cancellationToken)
    {
        if (_jobStore is null)
        {
            return new OpsGpQueueView { TotalActive = 0, Available = false, Buckets = [] };
        }

        var activeJobs = await _jobStore.ListActiveAsync(kind: null, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        var now = _timeProvider.GetUtcNow();
        return new OpsAlertDispatchView
        {
            DispatcherRunning = _alertHealth.IsDispatcherRunning,
            DispatcherEnabled = _alertHealth.IsDispatcherEnabled,
            StoragePollFailing = _alertHealth.IsStoragePollFailing,
            LastPollAt = _alertHealth.LastPollAt,
            PendingCount = backlog?.PendingCount,
            DeadLetteredCount = backlog?.DeadLetteredCount,
            RetryingCount = backlog?.RetryingCount,
            OldestItemAgeSeconds = backlog?.OldestItemAt is { } oldestItemAt
                ? Math.Max(0, (long)(now - oldestItemAt).TotalSeconds)
                : null,
            Channels = backlog?.Channels
                .OrderBy(static channel => channel.ChannelType)
                .Select(channel => new OpsAlertDispatchChannelView
                {
                    Channel = channel.ChannelType.ToExternalName(),
                    Paused = channel.IsPaused,
                    PendingCount = channel.PendingCount,
                    RetryingCount = channel.RetryingCount,
                    DeadLetteredCount = channel.DeadLetteredCount,
                    OldestItemAgeSeconds = Math.Max(0, (long)(now - channel.OldestItemAt).TotalSeconds),
                })
                .ToList() ?? [],
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
