// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Deterministic, evaluation-on-demand implementation of <see cref="IOpsFindingsService"/>. Each rule
/// is small, side-effect-free, and reads a single live signal; recommended actions route through the
/// existing <see cref="IOperationGateway"/> approval flow. No background loop and no model calls
/// (ADR-0028) — findings are computed only when requested.
/// </summary>
internal sealed class OpsFindingsService : IOpsFindingsService
{
    internal const string RequestedByAgent = "ops-findings";

    internal const string RuleAlertDispatchBacklog = "alert-dispatch-backlog";
    internal const string RulePlatformReleaseSkew = "platform-release-skew";
    internal const string RulePendingContractMigrations = "pending-contract-migrations";
    internal const string RuleGpQueueDepth = "gp-queue-depth";
    internal const string RuleDeployManualIntervention = "deploy-manual-intervention";
    internal const string RuleLocalBackendSubstrate = "local-backend-substrate-incompatible";
    internal const string RuleDbBoundedAdmissionPressure = "db-bounded-admission-pressure";
    internal const string RuleServingLatencySlo = "serving-latency-slo-breach";
    internal const string RulePlatformReleaseRuntimeDivergence = "platform-release-runtime-divergence";

    // BackendName of the in-process baseline batch backend (which reports a KubernetesJob target kind).
    private const string InProcessLocalBackendName = "local";

    private readonly IOptionsMonitor<OpsFindingsOptions> _options;
    private readonly IOptionsMonitor<ControlPlaneOptions> _controlPlaneOptions;
    private readonly IAlertDispatchHealth _alertHealth;
    private readonly IDeployPreflightProbe _deployProbe;
    private readonly IReadOnlyList<IBatchComputeBackend> _batchBackends;
    private readonly Func<string, string?> _environmentAccessor;
    private readonly IOperationGateway? _gateway;
    private readonly IWorkflowOperationStore? _workflowStore;
    private readonly IExecutionJobStore? _jobStore;
    private readonly IOpsDatabasePressureSignal? _databasePressureSignal;
    private readonly IRuntimeTunableAdmissionGate? _admissionGate;
    private readonly IOpsHealthRollupStore? _rollupStore;

    public OpsFindingsService(
        IOptionsMonitor<OpsFindingsOptions> options,
        IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
        IAlertDispatchHealth alertHealth,
        IDeployPreflightProbe deployProbe,
        IEnumerable<IBatchComputeBackend>? batchBackends = null,
        Func<string, string?>? environmentAccessor = null,
        IOperationGateway? gateway = null,
        IWorkflowOperationStore? workflowStore = null,
        IExecutionJobStore? jobStore = null,
        OpsFindingsExtendedSignals? extendedSignals = null)
    {
        _options = options;
        _controlPlaneOptions = controlPlaneOptions;
        _alertHealth = alertHealth;
        _deployProbe = deployProbe;
        _batchBackends = batchBackends?.ToList() ?? [];
        _environmentAccessor = environmentAccessor ?? SubstrateProfileResolver.ReadEnvironment;
        _gateway = gateway;
        _workflowStore = workflowStore;
        _jobStore = jobStore;
        _databasePressureSignal = extendedSignals?.DatabasePressureSignal;
        _admissionGate = extendedSignals?.AdmissionGate;
        _rollupStore = extendedSignals?.RollupStore;
    }

    public async Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var options = _options.CurrentValue;
        var findings = new List<OpsFinding>();

        EvaluateAlertDispatchBacklog(now, options, findings);
        EvaluateLocalBackendSubstrate(now, findings);
        EvaluatePlatformReleaseSkew(now, findings);
        EvaluateDbBoundedAdmissionPressure(now, options, findings);
        await EvaluatePendingContractMigrationsAsync(now, findings, cancellationToken).ConfigureAwait(false);
        await EvaluateGpQueueDepthAsync(now, options, findings, cancellationToken).ConfigureAwait(false);
        await EvaluateDeployManualInterventionAsync(now, findings, cancellationToken).ConfigureAwait(false);
        await EvaluateServingLatencySloAsync(now, options, findings, cancellationToken).ConfigureAwait(false);
        await EvaluatePlatformReleaseRuntimeDivergenceAsync(now, findings, cancellationToken).ConfigureAwait(false);

        // Most urgent first; stable ordering by rule then id keeps output deterministic.
        return findings
            .OrderByDescending(f => (int)f.Severity)
            .ThenBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<OpsFindingProposalResult> ProposeAsync(string findingId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(findingId))
        {
            return new OpsFindingProposalResult { Status = OpsFindingProposalStatus.FindingNotFound, FindingId = findingId ?? string.Empty };
        }

        var findings = await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var finding = findings.FirstOrDefault(f => string.Equals(f.Id, findingId, StringComparison.Ordinal));
        if (finding is null)
        {
            return new OpsFindingProposalResult { Status = OpsFindingProposalStatus.FindingNotFound, FindingId = findingId };
        }

        if (finding.RecommendedAction is null)
        {
            return new OpsFindingProposalResult { Status = OpsFindingProposalStatus.NoRecommendedAction, FindingId = findingId };
        }

        // Degraded mode: the operation gateway is only wired when the durable control-plane graph is
        // registered (which requires Redis — see Program.cs). Without it the server still evaluates
        // and serves findings, but their recommended fixes cannot be routed for approval. Report a
        // clear degraded outcome instead of failing to construct the service at startup (#2511).
        if (_gateway is null)
        {
            return new OpsFindingProposalResult
            {
                Status = OpsFindingProposalStatus.GatewayUnavailable,
                FindingId = findingId,
                Message = "The durable control-plane operation gateway is unavailable "
                    + "(it requires the durable backend / Redis); the recommended action cannot be proposed.",
            };
        }

        var action = finding.RecommendedAction;
        var request = new OperationGatewayRequest
        {
            Kind = action.Kind,
            ActionDiscriminator = action.ActionDiscriminator,
            RequestedByAgent = RequestedByAgent,
            Reason = action.Reason,
            ExecutionPayload = action.ExecutionPayload,
            // Idempotency-keyed on the deterministic finding id so re-proposing the same live
            // condition folds onto the same gateway operation rather than spawning duplicates.
            IdempotencyKey = findingId,
            AutonomyContext = new OperationGatewayAutonomyContext
            {
                FindingId = finding.Id,
                Rule = finding.Rule,
                ActionMarkedAutoSafe = action.AutoSafe,
                BlastRadius = Math.Max(1, action.BlastRadius),
                EvidenceRefs = finding.EvidenceRefs,
            },
        };

        var result = await _gateway.RouteAsync(request, cancellationToken).ConfigureAwait(false);

        return new OpsFindingProposalResult
        {
            Status = MapOutcome(result.Outcome),
            FindingId = findingId,
            ProposalId = result.ProposalId,
            ExecutionOperationId = result.ExecutionOperationId,
            Message = result.Message,
        };
    }

    private static OpsFindingProposalStatus MapOutcome(OperationGatewayOutcome outcome) => outcome switch
    {
        OperationGatewayOutcome.Executed => OpsFindingProposalStatus.Executed,
        OperationGatewayOutcome.ProposalCreated => OpsFindingProposalStatus.ProposalCreated,
        OperationGatewayOutcome.Blocked => OpsFindingProposalStatus.Blocked,
        OperationGatewayOutcome.Failed => OpsFindingProposalStatus.Failed,
        OperationGatewayOutcome.RolledBack => OpsFindingProposalStatus.RolledBack,
        OperationGatewayOutcome.Indeterminate => OpsFindingProposalStatus.Indeterminate,
        OperationGatewayOutcome.Canceled => OpsFindingProposalStatus.Canceled,
        _ => OpsFindingProposalStatus.NotSupported,
    };

    // Rule (a): alert-dispatch backlog / dead-letters over threshold. Dead-letters carry a real
    // recommended action now that the ops-action registry has a redrive actuator (#2579): re-enqueue
    // the dead-lettered rows for redelivery. A pending-only backlog (not yet dead-lettered) stays
    // informational — there is no safe automatic fix for deliveries that are merely lagging.
    private void EvaluateAlertDispatchBacklog(DateTimeOffset now, OpsFindingsOptions options, List<OpsFinding> findings)
    {
        if (!_alertHealth.IsDispatcherEnabled || _alertHealth.LastBacklog is not { } backlog)
        {
            return;
        }

        var deadLettersOverThreshold = backlog.DeadLetteredCount >= options.AlertDispatchDeadLetterThreshold;
        var backlogOverThreshold = backlog.PendingCount >= options.AlertDispatchPendingBacklogThreshold;
        if (!deadLettersOverThreshold && !backlogOverThreshold)
        {
            return;
        }

        var subject = new OpsFindingSubject { Channel = "alert-dispatch" };
        var severity = deadLettersOverThreshold ? OpsFindingSeverity.Critical : OpsFindingSeverity.Warning;

        OpsFindingRecommendedAction? action = null;
        string explanation;
        if (deadLettersOverThreshold)
        {
            action = new OpsFindingRecommendedAction
            {
                Kind = OperationClass.AdminConfigChange,
                ActionDiscriminator = OpsActionNames.RedriveDeadLetters,
                Summary = $"Redrive {backlog.DeadLetteredCount} dead-lettered alert dispatch row(s) for redelivery.",
                ExecutionPayload = OpsActionExecutionPayloads.RedriveDeadLetters(),
                Reason = $"The alert dispatcher has {backlog.DeadLetteredCount} dead-lettered notification(s) that exhausted retries.",
                AutoSafe = true,
                BlastRadius = backlog.DeadLetteredCount > int.MaxValue ? int.MaxValue : (int)Math.Max(1, backlog.DeadLetteredCount),
            };
            explanation = $"The alert dispatcher has {backlog.DeadLetteredCount} dead-lettered notification(s) that exhausted retries and require operator triage (pending backlog {backlog.PendingCount}). Redriving re-enqueues them for redelivery; if the underlying channel is unhealthy the same rows will dead-letter again, and repeated storms should be triaged by pausing the affected channel directly (no per-channel breakdown is available from this signal to safely automate that step yet).";
        }
        else
        {
            explanation = $"The alert dispatcher pending backlog is {backlog.PendingCount}, at or above the configured threshold of {options.AlertDispatchPendingBacklogThreshold}. Deliveries are lagging; check channel throughput and downstream availability. No automatic action is offered because a pending (not yet dead-lettered) backlog has no safe redrive target.";
        }

        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RuleAlertDispatchBacklog, subject),
            Rule = RuleAlertDispatchBacklog,
            Severity = severity,
            Title = deadLettersOverThreshold ? "Alert dispatch dead-letters require triage" : "Alert dispatch backlog is elevated",
            Explanation = explanation,
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = ["healthcheck:alert-dispatch", "GET /monitoring/health/comprehensive"],
            RecommendedAction = action,
        });
    }

    // Rule (f): database connection-pool pressure (utilization and/or acquisition timeouts over
    // threshold). Carries a db.tune_bounded_admission action ONLY when the runtime-tunable admission
    // gate is registered (Postgres provider) and has headroom to reduce below its current target;
    // otherwise informational.
    private void EvaluateDbBoundedAdmissionPressure(DateTimeOffset now, OpsFindingsOptions options, List<OpsFinding> findings)
    {
        if (_databasePressureSignal is null)
        {
            return;
        }

        var snapshot = _databasePressureSignal.GetSnapshot();
        var utilizationOverThreshold = snapshot.HasConnectionPoolUtilization
            && snapshot.ConnectionPoolUtilization >= options.DbPoolUtilizationThreshold;
        var timeoutsOverThreshold = snapshot.ConnectionAcquisitionTimeouts >= options.DbConnectionAcquisitionTimeoutThreshold;
        if (!utilizationOverThreshold && !timeoutsOverThreshold)
        {
            return;
        }

        var subject = new OpsFindingSubject();
        var evidence = new List<string> { "GET /api/v1/admin/observability/ops-health" };
        if (utilizationOverThreshold)
        {
            evidence.Add($"db-pool-utilization:{snapshot.ConnectionPoolUtilization:F2}");
        }

        if (timeoutsOverThreshold)
        {
            evidence.Add($"db-acquisition-timeouts:{snapshot.ConnectionAcquisitionTimeouts}");
        }

        var utilizationText = snapshot.HasConnectionPoolUtilization
            ? snapshot.ConnectionPoolUtilization.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)
            : "unavailable";

        OpsFindingRecommendedAction? action = null;
        string noActionReason = string.Empty;
        if (_admissionGate is null)
        {
            noActionReason = " No automatic action is offered because the bounded database admission gate is unavailable for the active data provider.";
        }
        else
        {
            var tuned = Math.Max(
                _admissionGate.MinLimit,
                (int)Math.Floor(_admissionGate.CurrentLimit * (1.0 - options.DbBoundedAdmissionTuneDownFraction)));
            if (tuned >= _admissionGate.CurrentLimit)
            {
                noActionReason = " No automatic action is offered because the bounded admission gate is already at its configured minimum.";
            }
            else
            {
                action = new OpsFindingRecommendedAction
                {
                    Kind = OperationClass.AdminConfigChange,
                    ActionDiscriminator = OpsActionNames.TuneBoundedAdmission,
                    Summary = $"Tune the bounded database admission target down to {tuned} (from {_admissionGate.CurrentLimit}).",
                    ExecutionPayload = OpsActionExecutionPayloads.TuneBoundedAdmission(tuned),
                    Reason = $"Database connection-pool pressure is elevated (utilization {utilizationText}, acquisition timeouts {snapshot.ConnectionAcquisitionTimeouts}); temporarily lower the bounded admission target to relieve pressure.",
                };
            }
        }

        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RuleDbBoundedAdmissionPressure, subject),
            Rule = RuleDbBoundedAdmissionPressure,
            Severity = OpsFindingSeverity.Critical,
            Title = "Database connection-pool pressure is elevated",
            Explanation = $"Database connection-pool pressure is elevated (utilization {utilizationText}, acquisition timeouts {snapshot.ConnectionAcquisitionTimeouts}, acquisition failures {snapshot.ConnectionAcquisitionFailures}).{noActionReason}",
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = evidence,
            RecommendedAction = action,
        });
    }

    // Rule (a2): the single-host local batch-compute backend(s) are registered on a substrate they
    // cannot work on (serverless, or multi-node without a shared work dir). Critical — this is the
    // fail-closed operator signal that replaces the previous silent re-queue loop, where a job launched
    // on the local backend was observed as "process lost" from a node that never launched it and
    // re-queued with no indication that the backend is fundamentally incompatible with the substrate.
    private void EvaluateLocalBackendSubstrate(DateTimeOffset now, List<OpsFinding> findings)
    {
        var localBackends = _batchBackends
            .Where(static backend =>
                backend.TargetKind == BatchComputeTargetKind.LocalProcess
                || string.Equals(backend.BackendName, InProcessLocalBackendName, StringComparison.Ordinal))
            .ToList();
        if (localBackends.Count == 0)
        {
            return;
        }

        var substrate = _controlPlaneOptions.CurrentValue.Substrate;
        var effectiveProfile = SubstrateProfileResolver.ResolveEffectiveProfile(substrate, _environmentAccessor);
        var compatibility = LocalBatchComputeSubstrate.Evaluate(effectiveProfile, substrate.SharedWorkDir);
        if (compatibility.IsCompatible)
        {
            return;
        }

        var backendNames = string.Join(", ", localBackends.Select(static b => b.BackendName).OrderBy(static n => n, StringComparer.Ordinal));
        var subject = new OpsFindingSubject { Channel = "batch-compute" };

        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RuleLocalBackendSubstrate, subject),
            Rule = RuleLocalBackendSubstrate,
            Severity = OpsFindingSeverity.Critical,
            Title = "Local batch backend cannot work on this substrate",
            Explanation = $"The local batch-compute backend(s) [{backendNames}] are registered, but the "
                + $"deployment substrate profile is '{effectiveProfile}'. {compatibility.Reason} Jobs targeting the "
                + "local backend will not run: route geoprocessing/import workloads to a remote batch backend, or "
                + "correct the substrate profile / configure a shared work directory. No automatic fix is offered "
                + "because the safe remediation is a deployment-topology decision.",
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = ["config:ControlPlane:Substrate", "GET /monitoring/health/comprehensive"],
            RecommendedAction = null,
        });
    }

    // Rule (b): platform-release skew (config-pin divergence). Informational, L1 guidance ONLY (#2556
    // spec revision 2026-07-06): a plane element here explicitly pins an artifact that differs from the
    // release, so the skew is config-derived, not something a runtime action can clear — a redeploy would
    // just be overridden by the pin again. See RulePlatformReleaseRuntimeDivergence for the sibling rule
    // that DOES offer a runtime action, for targets that pin nothing and have simply not converged yet.
    private void EvaluatePlatformReleaseSkew(DateTimeOffset now, List<OpsFinding> findings)
    {
        var skew = PlatformReleaseSkewProjector.Build(_controlPlaneOptions.CurrentValue);

        if (!skew.ReleaseDeclared || skew.IsCoVersioned || skew.SkewedIds.Count == 0)
        {
            return;
        }

        var subject = new OpsFindingSubject { ReleaseVersion = skew.ReleaseVersion };
        var evidence = new List<string> { "GET /api/v1/admin/deploy/preflight?includeDiagnostics=true" };
        evidence.AddRange(skew.SkewedIds.Select(id => $"skewed:{id}"));

        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RulePlatformReleaseSkew, subject),
            Rule = RulePlatformReleaseSkew,
            Severity = OpsFindingSeverity.Warning,
            Title = "Platform release is not co-versioned",
            Explanation = $"Declared platform release '{skew.ReleaseVersion}' is not co-versioned: {skew.SkewedIds.Count} plane(s) explicitly pin an artifact that diverges from the release ({string.Join(", ", skew.SkewedIds)}). This is L1 guidance only: a runtime action cannot clear config-derived skew, since the pinned configuration would simply re-assert itself on the next reconcile. Remove or update the explicit pin, then redeploy the plane to the release revision to restore co-versioning.",
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = evidence,
            RecommendedAction = null,
        });
    }

    // Rule (c): pending contract migrations blocking a coordinated deploy. Informational — explains the
    // expand -> deploy -> migrate -> contract discipline; contracting is an operator-sequenced step.
    private async Task EvaluatePendingContractMigrationsAsync(DateTimeOffset now, List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        var snapshot = await _deployProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var migration = snapshot.Migration;
        if (!migration.HasPendingContractScripts && migration.PendingContractScripts.Count == 0)
        {
            return;
        }

        var subject = new OpsFindingSubject();
        var pending = migration.PendingContractScripts;
        var evidence = new List<string> { "GET /api/v1/admin/deploy/preflight?includeDiagnostics=true" };
        evidence.AddRange(pending.Select(script => $"contract-script:{script}"));

        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RulePendingContractMigrations, subject),
            Rule = RulePendingContractMigrations,
            Severity = OpsFindingSeverity.Warning,
            Title = "Pending contract migrations block coordinated deploy",
            Explanation = $"{pending.Count} contract migration script(s) are pending, which holds coordinated-deploy readiness. Under the expand -> deploy -> migrate -> contract discipline, contract steps run only after the new code is fully rolled out and the prior schema is no longer read; run the pending contract migrations once the deploy is complete and verified. This is informational: contracting is an operator-sequenced step, not an automatic action.",
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = evidence,
            RecommendedAction = null,
        });
    }

    // Rule (d): GP queue depth sustained above threshold. Informational — surfaces a scale hint; there
    // is no safe automatic scale action to route in this slice.
    private async Task EvaluateGpQueueDepthAsync(DateTimeOffset now, OpsFindingsOptions options, List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        if (_jobStore is null)
        {
            return;
        }

        var activeJobs = await _jobStore.ListActiveAsync(kind: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var queueDepth = ControlPlaneTelemetry.ComputeQueueDepth(activeJobs);
        var totalActive = queueDepth.Sum(entry => entry.Count);
        if (totalActive < options.GpQueueDepthThreshold)
        {
            return;
        }

        var subject = new OpsFindingSubject();
        var breakdown = string.Join(", ", queueDepth.Select(entry => $"{entry.Status}/{entry.Backend}={entry.Count}"));

        findings.Add(new OpsFinding
        {
            Id = OpsFindingId.Create(RuleGpQueueDepth, subject),
            Rule = RuleGpQueueDepth,
            Severity = OpsFindingSeverity.Warning,
            Title = "Geoprocessing queue depth is high",
            Explanation = $"Active geoprocessing job depth is {totalActive} (queued/provisioning/running), at or above the configured threshold of {options.GpQueueDepthThreshold}. Breakdown by status/backend: {breakdown}. Consider scaling execution-worker capacity for the busiest backend; no automatic action is taken.",
            DetectedAt = now,
            Subject = subject,
            EvidenceRefs = ["metric:honua.execution.queue.depth"],
            RecommendedAction = null,
        });
    }

    // Rule (e): deploy operation stuck in ManualInterventionRequired. Critical. Offers a rollback
    // recommended action ONLY when a safe rollback target revision is derivable (the operation records
    // the revision it was moving away from); otherwise it is informational and says so.
    private async Task EvaluateDeployManualInterventionAsync(DateTimeOffset now, List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        if (_workflowStore is null)
        {
            return;
        }

        var active = await _workflowStore.ListActiveAsync(WorkflowOperationKind.Deploy, cancellationToken).ConfigureAwait(false);
        foreach (var operation in active)
        {
            if (operation.Status != WorkflowOperationStatus.ManualInterventionRequired)
            {
                continue;
            }

            var deploy = operation.Deploy;
            var subject = new OpsFindingSubject
            {
                OperationId = operation.OperationId,
                TargetId = deploy?.TargetId,
            };
            var evidence = new List<string>
            {
                $"operation:{operation.OperationId}",
                $"GET /api/v1/admin/deploy/operations/{operation.OperationId}",
            };

            // A robustly-derivable rollback target is the revision the operation was moving away from
            // (Deploy.CurrentRevision). When it is unknown we cannot craft a safe payload, so the
            // finding is informational.
            var canRollback = deploy is not null && !string.IsNullOrWhiteSpace(deploy.CurrentRevision);
            OpsFindingRecommendedAction? action = null;
            if (canRollback)
            {
                var rollbackPayload = new DeployExecutionPayload
                {
                    TargetId = deploy!.TargetId,
                    DesiredRevision = deploy.CurrentRevision!,
                    CurrentRevision = deploy.DesiredRevision,
                }.Serialize();

                action = new OpsFindingRecommendedAction
                {
                    Kind = OperationClass.Deploy,
                    Summary = $"Roll back deploy target '{deploy.TargetId}' to revision '{deploy.CurrentRevision}'.",
                    ExecutionPayload = rollbackPayload,
                    Reason = $"Deploy operation {operation.OperationId} requires manual intervention; roll back to the prior revision.",
                };
            }

            var explanation = canRollback
                ? $"Deploy operation '{operation.OperationId}' for target '{deploy!.TargetId}' is stuck in ManualInterventionRequired. A rollback to the prior revision '{deploy.CurrentRevision}' is proposable through the approval gateway."
                : $"Deploy operation '{operation.OperationId}' is stuck in ManualInterventionRequired, but no rollback action is offered: the prior (rollback-target) revision is not recorded on the operation, so a safe rollback payload cannot be derived. Investigate and remediate manually.";

            findings.Add(new OpsFinding
            {
                Id = OpsFindingId.Create(RuleDeployManualIntervention, subject),
                Rule = RuleDeployManualIntervention,
                Severity = OpsFindingSeverity.Critical,
                Title = "Deploy operation requires manual intervention",
                Explanation = explanation,
                DetectedAt = now,
                Subject = subject,
                EvidenceRefs = evidence,
                RecommendedAction = action,
            });
        }
    }

    // Rule (g): per-protocol serving-latency/error-rate SLO breach, evidenced from the persisted
    // ops-health rollup history (#2553) rather than the live in-process window, so a short blip does not
    // flap the finding. Informational — a generic protocol-level latency/error-rate breach has no single
    // safe automatic remediation (unlike the post-promotion case, there is no specific deploy to roll
    // back). Fails open when the rollup store is unavailable or errors (#2511-style graceful degradation).
    private async Task EvaluateServingLatencySloAsync(DateTimeOffset now, OpsFindingsOptions options, List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        if (_rollupStore is null)
        {
            return;
        }

        IReadOnlyList<OpsHealthLatencyRow> rows;
        try
        {
            var from = now.AddMinutes(-options.ServingLatencyLookbackMinutes);
            rows = await _rollupStore.ReadLatencyAsync(OpsHealthRollupTier.OneMinute, from, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        if (rows.Count == 0)
        {
            return;
        }

        var latestPerProtocol = rows
            .GroupBy(row => row.Point.Protocol, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => row.BucketStart).First().Point);

        foreach (var point in latestPerProtocol)
        {
            var latencyBreach = point.P95Ms >= options.ServingLatencyP95ThresholdMs;
            var errorRateBreach = point.ErrorRate >= options.ServingErrorRateThreshold;
            if (!latencyBreach && !errorRateBreach)
            {
                continue;
            }

            var subject = new OpsFindingSubject { Protocol = point.Protocol };

            findings.Add(new OpsFinding
            {
                Id = OpsFindingId.Create(RuleServingLatencySlo, subject),
                Rule = RuleServingLatencySlo,
                Severity = OpsFindingSeverity.Warning,
                Title = $"{point.Protocol} serving-latency/error-rate SLO breach",
                Explanation = $"Protocol '{point.Protocol}' p95 latency is {point.P95Ms:F0}ms (threshold {options.ServingLatencyP95ThresholdMs:F0}ms) and/or error rate is {point.ErrorRate:P2} (threshold {options.ServingErrorRateThreshold:P2}) over the last {options.ServingLatencyLookbackMinutes:F0} minute(s) of persisted history. No automatic action is offered: a generic protocol-level latency/error-rate breach has no single safe remediation.",
                DetectedAt = now,
                Subject = subject,
                EvidenceRefs = ["GET /api/v1/admin/observability/ops-health/history", $"protocol:{point.Protocol}"],
                RecommendedAction = null,
            });
        }
    }

    // Rule (h): platform-release runtime divergence (#2556 EXCLUSIVELY owns this rule; pinned divergence
    // contract from #2564). Evidence: per unpinned serving deploy target, the last-applied revision is
    // the DesiredRevision of the most recent terminal-Succeeded Deploy operation (#2562's per-target
    // terminal index); a target with NO terminal-succeeded operation is unknown and treated as divergent.
    // Config-pinned targets are SKIPPED here — that is RulePlatformReleaseSkew's concern, since
    // config-derived skew cannot be cleared at runtime. When divergent, the recommended action creates a
    // gateway-routed Deploy operation to the declared release artifact: the same per-target actuation the
    // platform-release converge API (#2564) performs, reusing the existing Deploy operation-gateway path
    // so this rule does not depend on that API's endpoint landing first.
    private async Task EvaluatePlatformReleaseRuntimeDivergenceAsync(DateTimeOffset now, List<OpsFinding> findings, CancellationToken cancellationToken)
    {
        if (_workflowStore is null)
        {
            return;
        }

        var controlPlane = _controlPlaneOptions.CurrentValue;
        var release = controlPlane.PlatformRelease.ToDefinition();
        if (release is null || !release.DeclaresServing)
        {
            return;
        }

        var declaredArtifact = release.ServingArtifactReference!;

        foreach (var target in controlPlane.DeployTargets)
        {
            if (string.IsNullOrWhiteSpace(target.TargetId) || !string.IsNullOrWhiteSpace(target.ArtifactReference))
            {
                // No target id to key on, or an explicit pin: pinned targets are the
                // platform-release-skew rule's concern, not this one's.
                continue;
            }

            var lastSucceeded = await _workflowStore
                .GetMostRecentSucceededDeployByTargetAsync(target.TargetId, cancellationToken)
                .ConfigureAwait(false);
            var lastAppliedRevision = lastSucceeded?.Deploy?.DesiredRevision;

            // Pinned contract (#2564): no terminal-Succeeded deploy for the target = unknown = divergent.
            var isDivergent = lastAppliedRevision is null
                || !string.Equals(lastAppliedRevision, declaredArtifact, StringComparison.Ordinal);
            if (!isDivergent)
            {
                continue;
            }

            var subject = new OpsFindingSubject { TargetId = target.TargetId, ReleaseVersion = release.Version };
            var evidence = new List<string>
            {
                "GET /api/v1/admin/deploy/preflight?includeDiagnostics=true",
                lastAppliedRevision is null
                    ? $"terminal-index:{target.TargetId}:none"
                    : $"terminal-index:{target.TargetId}:{lastAppliedRevision}",
            };

            var payload = new DeployExecutionPayload
            {
                TargetId = target.TargetId,
                DesiredRevision = declaredArtifact,
                CurrentRevision = lastAppliedRevision,
            }.Serialize();

            var lastAppliedDescription = lastAppliedRevision is null
                ? "no terminal-succeeded deploy operation is recorded for this target, so it is treated as divergent"
                : $"its last-applied revision is '{lastAppliedRevision}'";

            findings.Add(new OpsFinding
            {
                Id = OpsFindingId.Create(RulePlatformReleaseRuntimeDivergence, subject),
                Rule = RulePlatformReleaseRuntimeDivergence,
                Severity = OpsFindingSeverity.Warning,
                Title = "Deploy target has not converged to the declared platform release",
                Explanation = $"Serving target '{target.TargetId}' does not pin an explicit artifact, but {lastAppliedDescription}, which diverges from the declared platform release '{release.Version}' ('{declaredArtifact}'). Converging this target creates a gateway-routed Deploy operation to the declared artifact — the same per-target actuation the platform-release converge API performs (#2564); workers are unaffected by this action (they converge at the next GP dispatch via the release's worker-image projection).",
                DetectedAt = now,
                Subject = subject,
                EvidenceRefs = evidence,
                RecommendedAction = new OpsFindingRecommendedAction
                {
                    Kind = OperationClass.Deploy,
                    Summary = $"Deploy target '{target.TargetId}' to the declared platform release artifact '{declaredArtifact}'.",
                    ExecutionPayload = payload,
                    Reason = $"Target '{target.TargetId}' is divergent from declared platform release '{release.Version}'; converge to '{declaredArtifact}'.",
                },
            });
        }
    }
}
