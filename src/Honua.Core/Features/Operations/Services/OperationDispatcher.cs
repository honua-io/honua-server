// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Default <see cref="IOperationInvoker"/>. Resolves the descriptor + executor for an
/// operation id, runs the policy decision point, and only on
/// <see cref="PolicyDecisionKind.Allow"/> calls the executor. For Deny / RequireApproval /
/// DryRunFirst it returns a handle that reflects the decision WITHOUT touching the executor —
/// proving the guardrail seam holds even with the pass-through default policy.
/// </summary>
public sealed class OperationDispatcher : IOperationInvoker
{
    private readonly IOperationCatalog _catalog;
    private readonly Dictionary<string, IOperationExecutor> _executors;
    private readonly IOperationPolicyDecisionPoint _policy;
    private readonly IOperationApprovalBridge? _approvalBridge;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of <see cref="OperationDispatcher"/>.
    /// </summary>
    /// <param name="catalog">Operation grounding catalog.</param>
    /// <param name="executors">Registered operation executors.</param>
    /// <param name="policy">Policy decision point consulted before execution.</param>
    /// <param name="clock">Time provider used for envelope timestamps.</param>
    /// <param name="approvalBridge">Optional durable approval persistence seam.</param>
    public OperationDispatcher(
        IOperationCatalog catalog,
        IEnumerable<IOperationExecutor> executors,
        IOperationPolicyDecisionPoint policy,
        TimeProvider clock,
        IOperationApprovalBridge? approvalBridge = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);
        _catalog = catalog;
        _policy = policy;
        _clock = clock;
        _approvalBridge = approvalBridge;
        _executors = executors.ToDictionary(executor => executor.OperationId, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var executor = ResolveExecutor(request.OperationId);
        return await executor.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var createdAt = _clock.GetUtcNow();
        var operationInstanceId = $"opinst-{Guid.NewGuid():N}";
        var correlationId = string.IsNullOrWhiteSpace(context.CorrelationId)
            ? $"corr-{Guid.NewGuid():N}"
            : context.CorrelationId;
        var invocationContext = context with
        {
            OperationInstanceId = operationInstanceId,
            CorrelationId = correlationId,
        };

        var descriptor = await _catalog.GetDescriptorAsync(request.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new OperationNotFoundException(request.OperationId);
        var executor = ResolveExecutor(request.OperationId);

        var decision = await _policy
            .EvaluateAsync(descriptor, request, invocationContext, cancellationToken)
            .ConfigureAwait(false);

        // Guardrail seam: anything other than Allow short-circuits the executor.
        if (decision.Kind != PolicyDecisionKind.Allow)
        {
            return await BuildDecisionHandleAsync(
                    descriptor,
                    request,
                    invocationContext,
                    decision,
                    createdAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var executed = await executor.SubmitAsync(request, invocationContext, cancellationToken).ConfigureAwait(false);
        return executed with
        {
            OperationInstanceId = operationInstanceId,
            OperationId = descriptor.OperationId,
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            UpdatedAt = _clock.GetUtcNow(),
            AuthorizationOutcome = invocationContext.AuthorizationOutcome,
            PolicyDecision = PolicyDecisionKind.Allow,
        };
    }

    private IOperationExecutor ResolveExecutor(string operationId)
        => _executors.TryGetValue(operationId, out var executor)
            ? executor
            : throw new OperationNotFoundException(operationId);

    private async Task<OperationHandle> BuildDecisionHandleAsync(
        OperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        // Map each non-Allow decision onto its own structured handle status. Deny and
        // DryRunFirst are distinct terminal outcomes (no side effect occurred), separate
        // from RequireApproval which routes to the approval lane.
        var status = decision.Kind switch
        {
            PolicyDecisionKind.RequireApproval => OperationHandleStatus.RequiresApproval,
            PolicyDecisionKind.DryRunFirst => OperationHandleStatus.DryRunRequired,
            PolicyDecisionKind.Deny => OperationHandleStatus.Denied,
            _ => OperationHandleStatus.Failed
        };

        string? proposalId = null;
        string? auditId = null;
        var reason = decision.Reason ?? decision.Kind.ToString();

        if (decision.Kind == PolicyDecisionKind.RequireApproval)
        {
            if (_approvalBridge is null)
            {
                status = OperationHandleStatus.Failed;
                reason = "Approval is required, but durable proposal infrastructure is unavailable.";
            }
            else
            {
                var approval = await _approvalBridge
                    .CreateProposalAsync(descriptor, request, context, decision, cancellationToken)
                    .ConfigureAwait(false);
                if (!approval.IsDurable ||
                    string.IsNullOrWhiteSpace(approval.ProposalId) ||
                    string.IsNullOrWhiteSpace(approval.AuditId))
                {
                    status = OperationHandleStatus.Failed;
                    reason = approval.Reason
                        ?? "Approval is required, but durable proposal or audit persistence failed.";
                }
                else
                {
                    proposalId = approval.ProposalId;
                    auditId = approval.AuditId;
                    reason = approval.Reason ?? reason;
                }
            }
        }

        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId
                ?? throw new InvalidOperationException("The canonical operation instance id was not assigned."),
            OperationId = descriptor.OperationId,
            CorrelationId = context.CorrelationId
                ?? throw new InvalidOperationException("The canonical correlation id was not assigned."),
            Status = status,
            ProposalId = proposalId,
            AuditId = auditId,
            CreatedAt = createdAt,
            UpdatedAt = _clock.GetUtcNow(),
            AuthorizationOutcome = context.AuthorizationOutcome,
            PolicyDecision = decision.Kind,

            // Only RequireApproval routes to an approval lane; Deny/DryRunFirst carry none.
            ApprovalLane = decision.Kind == PolicyDecisionKind.RequireApproval
                ? decision.ApprovalLane
                : null,
            Reason = reason,
        };
    }
}
