// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Gateway executor for admin configuration changes. Applies the change through
/// the shared <see cref="IAdminConfigChangeApplier"/> so direct-execute and
/// post-approval apply run the same path (#1693).
/// </summary>
internal sealed class AdminConfigOperationExecutor(IAdminConfigChangeApplier applier) : IOperationExecutor
{
    public OperationClass OperationClass => OperationClass.AdminConfigChange;

    public Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = new OperationProposalPlan
        {
            Summary = request.Reason ?? "Admin configuration change",
            RiskLevel = ProposalRiskLevel.Medium,
            ExecutionPayload = request.ExecutionPayload,
        };

        return Task.FromResult<OperationProposalPlan?>(plan);
    }

    public Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
        => applier.ApplyAsync(executionPayload, cancellationToken);
}
