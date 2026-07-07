// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// Composes the server-authoritative aggregated operational status: one server-computed verdict plus
/// per-domain rollups and, when configured, an availability SLO snapshot. Built on the existing
/// deterministic ops spine — it reuses <see cref="IOpsHealthSnapshotService"/> (which already stitches
/// health, serving latency, GP queue, alert dispatch, deploy readiness, and database utilization) and
/// <see cref="IOpsFindingsService"/> rather than re-wiring the low-level seams, and adds the
/// deploy-operation state counts and telemetry-backend posture on top.
/// </summary>
internal interface IOperateStatusService
{
    /// <summary>Builds the aggregated operate status.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The composed status response.</returns>
    Task<OperateStatusResponse> GetAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class OperateStatusService : IOperateStatusService
{
    /// <summary>The payload schema version. Additive changes bump the minor component.</summary>
    internal const string SchemaVersion = "1.0";

    private const int MaxTopFindings = 5;

    private readonly IOpsHealthSnapshotService _healthSnapshot;
    private readonly IOpsFindingsService _findings;
    private readonly IOptionsMonitor<OperateSloOptions> _sloOptions;
    private readonly IOptionsMonitor<ControlPlaneOptions> _controlPlaneOptions;
    private readonly IWorkflowOperationStore? _workflowStore;

    public OperateStatusService(
        IOpsHealthSnapshotService healthSnapshot,
        IOpsFindingsService findings,
        IOptionsMonitor<OperateSloOptions> sloOptions,
        IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
        IWorkflowOperationStore? workflowStore = null)
    {
        _healthSnapshot = healthSnapshot;
        _findings = findings;
        _sloOptions = sloOptions;
        _controlPlaneOptions = controlPlaneOptions;
        _workflowStore = workflowStore;
    }

    public async Task<OperateStatusResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _healthSnapshot.GetAsync(cancellationToken).ConfigureAwait(false);
        var findings = await _findings.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var deploys = await BuildDeploysViewAsync(cancellationToken).ConfigureAwait(false);

        var jobs = BuildJobsView(snapshot);
        var alerts = BuildAlertsView(snapshot);
        var migrations = BuildMigrationsView(snapshot);
        var findingsView = BuildFindingsView(findings);
        var telemetryBackends = BuildTelemetryBackendsView();
        var slo = OperateSloEvaluator.Evaluate(_sloOptions.CurrentValue, HonuaTelemetry.GetServingLatencySnapshot());

        var signals = new OperateStatusSignals(
            HealthRollupStatus: snapshot.OverallStatus,
            CriticalFindingRules: findings
                .Where(f => f.Severity == OpsFindingSeverity.Critical)
                .Select(f => f.Rule)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            ParkedDeploys: deploys.Parked,
            AlertDeadLettered: alerts.DeadLettered ?? 0,
            AlertDispatchImpaired: (alerts.Enabled && !alerts.DispatcherRunning) || alerts.StoragePollFailing,
            SloErrorBudgetExhausted: OperateSloEvaluator.IsErrorBudgetExhausted(slo));

        var (status, reasons) = OperateStatusVerdictEvaluator.Evaluate(signals);

        return new OperateStatusResponse
        {
            SchemaVersion = SchemaVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            Status = OperateStatusVerdictEvaluator.ToWire(status),
            Reasons = reasons,
            Domains = new OperateStatusDomains
            {
                Deploys = deploys,
                Jobs = jobs,
                Alerts = alerts,
                Migrations = migrations,
                Findings = findingsView,
                TelemetryBackends = telemetryBackends,
            },
            Slo = slo,
        };
    }

    private async Task<OperateDeploysView> BuildDeploysViewAsync(CancellationToken cancellationToken)
    {
        const string source = "GET /api/v1/admin/deploy/operations/{operationId}";

        if (_workflowStore is null)
        {
            return new OperateDeploysView
            {
                Source = source,
                Available = false,
                Active = 0,
                Parked = 0,
                AwaitingApproval = 0,
                ByStatus = new Dictionary<string, int>(StringComparer.Ordinal),
                LastOutcome = null,
            };
        }

        var operations = await _workflowStore.ListActiveAsync(WorkflowOperationKind.Deploy, cancellationToken).ConfigureAwait(false);

        var byStatus = operations
            .GroupBy(o => o.Status)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);

        var active = Count(byStatus, WorkflowOperationStatus.Submitted)
            + Count(byStatus, WorkflowOperationStatus.Reconciling)
            + Count(byStatus, WorkflowOperationStatus.RollbackRequested);

        var lastOutcome = operations
            .OrderByDescending(o => o.UpdatedAt)
            .Select(o => o.Status.ToString())
            .FirstOrDefault();

        return new OperateDeploysView
        {
            Source = source,
            Available = true,
            Active = active,
            Parked = Count(byStatus, WorkflowOperationStatus.ManualInterventionRequired),
            AwaitingApproval = Count(byStatus, WorkflowOperationStatus.AwaitingApproval),
            ByStatus = byStatus,
            LastOutcome = lastOutcome,
        };

        static int Count(IReadOnlyDictionary<string, int> byStatus, WorkflowOperationStatus status)
            => byStatus.TryGetValue(status.ToString(), out var count) ? count : 0;
    }

    private static OperateJobsView BuildJobsView(OpsHealthSnapshotResponse snapshot)
    {
        var gp = snapshot.Geoprocessing;
        var buckets = gp.Buckets
            .Select(b => new OperateJobBucketView { Status = b.Status, Backend = b.Backend, Count = b.Count })
            .ToList();

        var queued = buckets
            .Where(b => string.Equals(b.Status, nameof(ExecutionJobStatus.Queued), StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Count);
        var running = buckets
            .Where(b => !string.Equals(b.Status, nameof(ExecutionJobStatus.Queued), StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Count);

        var backends = buckets
            .Select(b => b.Backend)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToList();

        return new OperateJobsView
        {
            Source = "GET /api/v1/admin/observability/ops-health",
            Available = gp.Available,
            TotalActive = gp.TotalActive,
            Queued = queued,
            Running = running,
            Backends = backends,
            ByStatusBackend = buckets,
            // The substrate-profile resolver / local-backend compatibility evaluator are not on this
            // branch yet; the shape is present and reports "not evaluated" until that lands (see PR
            // fix/substrate-fail-closed).
            Substrate = new OperateSubstrateView
            {
                Evaluated = false,
                Profile = null,
                Compatible = null,
                Note = "Substrate compatibility is not evaluated on this build; local-backend "
                    + "substrate posture is surfaced by the substrate-fail-closed work.",
            },
        };
    }

    private static OperateAlertsView BuildAlertsView(OpsHealthSnapshotResponse snapshot)
    {
        var dispatch = snapshot.AlertDispatch;
        return new OperateAlertsView
        {
            Source = "GET /api/v1/admin/observability/alerts",
            Enabled = dispatch.DispatcherEnabled,
            DispatcherRunning = dispatch.DispatcherRunning,
            StoragePollFailing = dispatch.StoragePollFailing,
            Open = dispatch.PendingCount,
            DeadLettered = dispatch.DeadLetteredCount,
            LastPollAt = dispatch.LastPollAt,
            // Per-channel delivery circuit-breaker enumeration is not on this branch; empty until the
            // alert circuit-breaker work exposes a state snapshot.
            ChannelCircuits = [],
        };
    }

    private static OperateMigrationsView BuildMigrationsView(OpsHealthSnapshotResponse snapshot)
    {
        var deploy = snapshot.Deploy;
        var pending = deploy.PendingMigrationsCount;
        var pendingContract = deploy.PendingContractScriptsCount;

        var classification = pendingContract > 0
            ? "contract-pending"
            : pending > 0 ? "expand-only" : "none";

        return new OperateMigrationsView
        {
            Source = "GET /api/v1/admin/observability/migrations",
            Pending = pending,
            PendingContract = pendingContract,
            UpgradeRequired = pending > 0 || pendingContract > 0,
            Classification = classification,
        };
    }

    private static OperateFindingsView BuildFindingsView(IReadOnlyList<OpsFinding> findings)
    {
        var bySeverity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["critical"] = findings.Count(f => f.Severity == OpsFindingSeverity.Critical),
            ["warning"] = findings.Count(f => f.Severity == OpsFindingSeverity.Warning),
            ["info"] = findings.Count(f => f.Severity == OpsFindingSeverity.Info),
        };

        // findings arrive ordered by descending severity from the engine; take the top N verbatim.
        var top = findings
            .Take(MaxTopFindings)
            .Select(f => new OperateFindingSummaryView
            {
                Id = f.Id,
                Rule = f.Rule,
                Severity = f.Severity.ToString(),
                Title = f.Title,
            })
            .ToList();

        return new OperateFindingsView
        {
            Source = "GET /api/v1/admin/observability/findings",
            Total = findings.Count,
            BySeverity = bySeverity,
            Top = top,
        };
    }

    private OperateTelemetryBackendsView BuildTelemetryBackendsView()
    {
        var connections = _controlPlaneOptions.CurrentValue.TelemetryConnections;
        var backends = connections
            .Select(c => new OperateTelemetryBackendView
            {
                ConnectionId = c.ConnectionId,
                Provider = string.IsNullOrWhiteSpace(c.Provider) ? "prometheus" : c.Provider,
                // These are deploy-gating query providers, not liveness-probed on the status path.
                ReachabilityPosture = "configured-not-probed",
            })
            .OrderBy(b => b.ConnectionId, StringComparer.Ordinal)
            .ToList();

        return new OperateTelemetryBackendsView
        {
            Source = "ControlPlane:TelemetryConnections",
            Configured = backends.Count,
            Backends = backends,
        };
    }
}
