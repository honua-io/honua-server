// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
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

    private readonly IOptionsMonitor<OpsFindingsOptions> _options;
    private readonly IOptionsMonitor<ControlPlaneOptions> _controlPlaneOptions;
    private readonly IAlertDispatchHealth _alertHealth;
    private readonly IDeployPreflightProbe _deployProbe;
    private readonly IOperationGateway? _gateway;
    private readonly IWorkflowOperationStore? _workflowStore;
    private readonly IExecutionJobStore? _jobStore;

    public OpsFindingsService(
        IOptionsMonitor<OpsFindingsOptions> options,
        IOptionsMonitor<ControlPlaneOptions> controlPlaneOptions,
        IAlertDispatchHealth alertHealth,
        IDeployPreflightProbe deployProbe,
        IOperationGateway? gateway = null,
        IWorkflowOperationStore? workflowStore = null,
        IExecutionJobStore? jobStore = null)
    {
        _options = options;
        _controlPlaneOptions = controlPlaneOptions;
        _alertHealth = alertHealth;
        _deployProbe = deployProbe;
        _gateway = gateway;
        _workflowStore = workflowStore;
        _jobStore = jobStore;
    }

    public async Task<IReadOnlyList<OpsFinding>> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var options = _options.CurrentValue;
        var findings = new List<OpsFinding>();

        EvaluateAlertDispatchBacklog(now, options, findings);
        EvaluatePlatformReleaseSkew(now, findings);
        await EvaluatePendingContractMigrationsAsync(now, findings, cancellationToken).ConfigureAwait(false);
        await EvaluateGpQueueDepthAsync(now, options, findings, cancellationToken).ConfigureAwait(false);
        await EvaluateDeployManualInterventionAsync(now, findings, cancellationToken).ConfigureAwait(false);

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
            RequestedByAgent = RequestedByAgent,
            Reason = action.Reason,
            ExecutionPayload = action.ExecutionPayload,
            // Idempotency-keyed on the deterministic finding id so re-proposing the same live
            // condition folds onto the same gateway operation rather than spawning duplicates.
            IdempotencyKey = findingId,
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
        _ => OpsFindingProposalStatus.NotSupported,
    };

    // Rule (a): alert-dispatch backlog / dead-letters over threshold. Informational — points the
    // operator at channel health; there is no safe automatic fix for a delivery backlog.
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
        var explanation = deadLettersOverThreshold
            ? $"The alert dispatcher has {backlog.DeadLetteredCount} dead-lettered notification(s) that exhausted retries and require operator triage (pending backlog {backlog.PendingCount}). Inspect the affected notification channels' health and re-drive or discard the dead-lettered rows."
            : $"The alert dispatcher pending backlog is {backlog.PendingCount}, at or above the configured threshold of {options.AlertDispatchPendingBacklogThreshold}. Deliveries are lagging; check channel throughput and downstream availability.";

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
            RecommendedAction = null,
        });
    }

    // Rule (b): platform-release skew. Informational — a safe automatic fix is not robustly derivable
    // (redeploying each skewed plane needs an approved per-target revision), so we surface the skew
    // and its skewed ids for an operator to reconcile.
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
            Explanation = $"Declared platform release '{skew.ReleaseVersion}' is not co-versioned: {skew.SkewedIds.Count} plane(s) are skewed from the release ({string.Join(", ", skew.SkewedIds)}). No automatic fix is offered because a safe reconciliation requires an operator-approved revision for each skewed plane; redeploy the skewed planes to the release revision to restore co-versioning.",
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
}
