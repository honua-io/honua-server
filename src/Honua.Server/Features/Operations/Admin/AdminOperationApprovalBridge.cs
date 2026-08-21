// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Operations.Admin;

/// <summary>
/// Bridges a RequireApproval decision from the operation catalog into the durable shared
/// proposal store. The proposal payload is hidden by the proposal API and contains the original
/// bounded authorization context required to execute after approval.
/// </summary>
internal sealed class AdminOperationApprovalBridge(IServiceProvider services)
    : IOperationApprovalProposalBridge
{
    public async Task<OperationHandle> CreateProposalAsync(
        OperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision,
        CancellationToken cancellationToken = default)
    {
        var gateway = services.GetService<IOperationGateway>();
        if (gateway is null)
        {
            return new OperationHandle
            {
                OperationId = request.OperationId,
                HandleId = $"op-{Guid.NewGuid():N}"[..32],
                Status = OperationHandleStatus.RequiresApproval,
                ApprovalLane = decision.ApprovalLane,
                Reason = "Approval is required, but durable proposals are unavailable because the control-plane store is not configured.",
            };
        }

        var payload = JsonSerializer.Serialize(
            new ApprovedAdminOperationPayload
            {
                Action = request.OperationId,
                Request = request,
                Context = context,
            },
            OperationsJsonContext.Default.ApprovedAdminOperationPayload);
        var result = await gateway.CreateApprovalProposalAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.PublishedOperation,
            ActionDiscriminator = request.OperationId,
            RequestedBy = context.PrincipalId,
            RequestedByAgent = context.PrincipalId,
            Reason = decision.Reason ?? descriptor.Title,
            CorrelationId = context.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            ExecutionPayload = payload,
            Plan = new OperationProposalPlan
            {
                Summary = descriptor.Title,
                RiskLevel = descriptor.Policy.BlastRadiusClass == OperationBlastRadiusClass.DeploymentScope
                    || descriptor.Policy.SideEffectClass == OperationSideEffectClass.DestroysState
                    ? ProposalRiskLevel.High
                    : ProposalRiskLevel.Medium,
                ExecutionPayload = payload,
            },
        }, cancellationToken).ConfigureAwait(false);

        if (result.Outcome != OperationGatewayOutcome.ProposalCreated
            || string.IsNullOrWhiteSpace(result.ProposalId))
        {
            return new OperationHandle
            {
                OperationId = request.OperationId,
                HandleId = $"op-{Guid.NewGuid():N}"[..32],
                Status = OperationHandleStatus.Failed,
                Reason = result.Message ?? "The approval proposal could not be created.",
            };
        }

        return new OperationHandle
        {
            OperationId = request.OperationId,
            HandleId = result.ProposalId,
            Status = OperationHandleStatus.RequiresApproval,
            ApprovalLane = decision.ApprovalLane ?? "admin-operation-proposals",
            Reason = decision.Reason ?? "Human approval is required.",
        };
    }
}

/// <summary>Durable payload for an approved admin catalog operation.</summary>
internal sealed record ApprovedAdminOperationPayload
{
    public required string Action { get; init; }

    public required OperationRequest Request { get; init; }

    public required OperationPolicyContext Context { get; init; }
}

/// <summary>Runs a stored catalog operation directly after the proposal has been approved.</summary>
internal sealed class ApprovedAdminOperationRunner(
    IEnumerable<Honua.Core.Features.Operations.Abstractions.IOperationExecutor> executors)
{
    private readonly Dictionary<string, Honua.Core.Features.Operations.Abstractions.IOperationExecutor> _executors =
        executors.ToDictionary(executor => executor.OperationId, StringComparer.Ordinal);

    public async Task<string?> ExecuteAsync(string? executionPayload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executionPayload))
        {
            throw new InvalidOperationException("The approved admin operation payload is missing.");
        }

        var payload = JsonSerializer.Deserialize(
            executionPayload,
            OperationsJsonContext.Default.ApprovedAdminOperationPayload)
            ?? throw new InvalidOperationException("The approved admin operation payload is invalid.");
        if (!AdminOperationManifest.Contains(payload.Action)
            || !_executors.TryGetValue(payload.Action, out var executor))
        {
            throw new InvalidOperationException($"Approved admin operation '{payload.Action}' is not registered.");
        }

        var handle = await executor.SubmitAsync(payload.Request, payload.Context, cancellationToken)
            .ConfigureAwait(false);
        if (handle.Status is OperationHandleStatus.Failed
            or OperationHandleStatus.Denied
            or OperationHandleStatus.RequiresApproval
            or OperationHandleStatus.DryRunRequired)
        {
            throw new InvalidOperationException(
                $"Approved admin operation '{payload.Action}' did not execute: {handle.Reason ?? handle.Status.ToString()}.");
        }

        return handle.JobId ?? handle.HandleId;
    }
}

/// <summary>
/// Shared control-plane executor that resumes a published operation after human approval.
/// It resolves the operation runner in a fresh scope so scoped admin services remain valid.
/// </summary>
internal sealed class PublishedOperationControlPlaneExecutor(IServiceScopeFactory scopeFactory)
    : Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor
{
    public OperationClass OperationClass => OperationClass.PublishedOperation;

    public Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
        {
            Summary = request.Reason ?? "Published admin operation",
            RiskLevel = ProposalRiskLevel.Medium,
            ExecutionPayload = request.ExecutionPayload,
        });

    public async Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApprovedAdminOperationRunner>()
            .ExecuteAsync(executionPayload, cancellationToken)
            .ConfigureAwait(false);
    }
}
