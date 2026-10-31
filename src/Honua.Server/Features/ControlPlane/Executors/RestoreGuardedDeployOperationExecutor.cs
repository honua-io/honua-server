// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Guardrails.Domain;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Deploy executor that restores a verified pre-migration backup before a reader-incompatible
/// rollback starts the prior image. Forward deploys and compatible rollbacks pass through.
/// A failed restore throws, so the gateway cannot record an operation id as success.
/// </summary>
internal sealed class RestoreGuardedDeployOperationExecutor(
    DeployOperationExecutor inner,
    PreMigrationRestoreCoordinator coordinator,
    IPreMigrationRestoreAudit? audit = null,
    IOptions<RestoreCellOptions>? restoreOptions = null,
    DurableRestoreQueue? queue = null,
    IOptionsMonitor<ControlPlaneOptions>? controlPlane = null) : IOperationExecutor
{
    public OperationClass OperationClass => OperationClass.Deploy;

    /// <inheritdoc />
    public async Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await inner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = DeployExecutionPayload.Parse(request.ExecutionPayload);
        if (plan is null || payload is not { Rollback: true } || IsLocalCompose(payload))
        {
            return plan;
        }

        var decision = await coordinator.AssessAsync(payload, cancellationToken).ConfigureAwait(false);
        if (decision.Kind == RestoreDecisionKind.Refuse)
        {
            var reasons = plan.BlockingReasons.ToList();
            reasons.Add(decision.Code ?? PreMigrationRestoreCodes.BackupRequired);
            return plan with
            {
                BlockingReasons = reasons,
                RiskLevel = ProposalRiskLevel.High,
            };
        }

        if (decision.Kind == RestoreDecisionKind.RestoreBeforeStart)
        {
            var warnings = plan.Warnings.ToList();
            warnings.Add(decision.Message ?? "Restore the verified backup before the prior image starts.");
            return plan with
            {
                Warnings = warnings,
                RiskLevel = ProposalRiskLevel.High,
            };
        }

        return plan;
    }

    /// <inheritdoc />
    public async Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
    {
        var payload = DeployExecutionPayload.Parse(executionPayload);
        if (payload is not null && IsLocalCompose(payload))
        {
            if (queue is null) throw new InvalidOperationException(PreMigrationRestoreCodes.TransportUnavailable);
            return await queue.EnqueueAsync(request, executionPayload!, cancellationToken).ConfigureAwait(false);
        }
        if (payload is not { Rollback: true })
        {
            return await inner.ExecuteAsync(request, executionPayload, cancellationToken).ConfigureAwait(false);
        }

        _ = restoreOptions;
        var decision = await coordinator.AssessAsync(payload, cancellationToken).ConfigureAwait(false);
        if (decision.Kind == RestoreDecisionKind.Refuse)
            throw new InvalidOperationException(decision.Code ?? PreMigrationRestoreCodes.BackupRequired);
        if (decision.Kind == RestoreDecisionKind.NotRequired)
            return await inner.ExecuteAsync(request, executionPayload, cancellationToken).ConfigureAwait(false);
        if (queue is null)
            throw new InvalidOperationException(PreMigrationRestoreCodes.TransportUnavailable);
        return await queue.EnqueueAsync(request, executionPayload!, cancellationToken).ConfigureAwait(false);
    }

    // Local Compose verifies the actual project journal, image and backup inside the sidecar.
    // It uses the same Core policy; cloud assessment cannot substitute its revision catalog for it.
    private bool IsLocalCompose(DeployExecutionPayload payload)
        => controlPlane?.CurrentValue.DeployTargets.Any(target =>
            target.TargetKind == DeployTargetKind.LocalCompose
            && string.Equals(target.TargetId, payload.TargetId, StringComparison.Ordinal)) == true;

    /// <inheritdoc />
    public async Task<OperationBackendStatus?> GetBackendStatusAsync(
        string executionOperationId,
        CancellationToken cancellationToken = default)
    {
        var durable = queue is null ? null : await queue.GetStatusAsync(executionOperationId, cancellationToken).ConfigureAwait(false);
        if (durable is not null) return durable;
        var live = await inner.GetBackendStatusAsync(executionOperationId, cancellationToken).ConfigureAwait(false);
        if (live is not null || audit is null)
        {
            return live;
        }

        return await audit.FindAsync(executionOperationId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Starts the prior image through the existing deploy workflow, and only when the gate calls it.</summary>
internal sealed class DeployPriorImageStarter(DeployOperationExecutor inner, TimeSpan budget) : IPriorImageStarter
{
    /// <inheritdoc />
    public async Task<string?> StartAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken)
    {
        var operationId = await inner.ExecuteAsync(request, executionPayload, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return null;
        }

        var serving = await PriorImageReadiness.WaitAsync(
                cancellationToken => inner.GetBackendStatusAsync(operationId, cancellationToken),
                budget,
                TimeSpan.FromSeconds(2),
                cancellationToken)
            .ConfigureAwait(false);
        return serving ? operationId : null;
    }
}

/// <summary>Polls a deploy workflow until it succeeds, fails, or the budget ends.</summary>
internal static class PriorImageReadiness
{
    /// <summary>
    /// Returns true only when a read reports <see cref="OperationBackendState.Succeeded"/>.
    /// A terminal failure returns false without waiting out the budget.
    /// </summary>
    public static async Task<bool> WaitAsync(
        Func<CancellationToken, Task<OperationBackendStatus?>> read,
        TimeSpan budget,
        TimeSpan poll,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        var deadline = DateTime.UtcNow + (budget < TimeSpan.Zero ? TimeSpan.Zero : budget);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await read(cancellationToken).ConfigureAwait(false);
            if (status?.State == OperationBackendState.Succeeded)
            {
                return true;
            }

            if (status?.State is OperationBackendState.Failed
                or OperationBackendState.Cancelled
                or OperationBackendState.Indeterminate)
            {
                return false;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            var remaining = deadline - DateTime.UtcNow;
            var delay = poll < remaining ? poll : remaining;
            if (delay <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}
