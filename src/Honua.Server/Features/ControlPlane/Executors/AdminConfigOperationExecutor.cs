// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

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
