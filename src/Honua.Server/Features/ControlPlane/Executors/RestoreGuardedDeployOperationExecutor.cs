// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Deploy executor that restores a verified pre-migration backup before a reader-incompatible
/// rollback starts the prior image. Forward deploys and compatible rollbacks pass through.
/// A failed restore throws, so the gateway cannot record an operation id as success.
/// </summary>
internal sealed class RestoreGuardedDeployOperationExecutor(
    DeployOperationExecutor inner,
    PreMigrationRestoreCoordinator coordinator) : IOperationExecutor
{
    public OperationClass OperationClass => OperationClass.Deploy;

    /// <inheritdoc />
    public async Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await inner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = DeployExecutionPayload.Parse(request.ExecutionPayload);
        if (plan is null || payload is not { Rollback: true })
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
        if (payload is not { Rollback: true })
        {
            return await inner.ExecuteAsync(request, executionPayload, cancellationToken).ConfigureAwait(false);
        }

        var completion = await coordinator.ExecuteAsync(
                payload,
                request,
                new DeployPriorImageStarter(inner),
                cancellationToken)
            .ConfigureAwait(false);
        if (!ReaderIncompatibleRestorePolicy.IsReportedSuccess(completion))
        {
            throw new InvalidOperationException(completion.Code ?? PreMigrationRestoreCodes.RestoreIncomplete);
        }

        return completion.OperationId;
    }

    /// <inheritdoc />
    public Task<OperationBackendStatus?> GetBackendStatusAsync(
        string executionOperationId,
        CancellationToken cancellationToken = default)
        => inner.GetBackendStatusAsync(executionOperationId, cancellationToken);
}

/// <summary>Starts the prior image through the existing deploy workflow, and only when the gate calls it.</summary>
internal sealed class DeployPriorImageStarter(DeployOperationExecutor inner) : IPriorImageStarter
{
    /// <inheritdoc />
    public Task<string?> StartAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken)
        => inner.ExecuteAsync(request, executionPayload, cancellationToken);
}
