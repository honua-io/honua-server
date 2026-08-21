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
    private readonly TimeProvider _clock;
    private readonly IOperationApprovalProposalBridge? _approvalBridge;
    private readonly Func<string, IOperationExecutor?>? _fallbackExecutorResolver;

    /// <summary>
    /// Initializes a new instance of <see cref="OperationDispatcher"/>.
    /// </summary>
    /// <param name="catalog">Operation grounding catalog.</param>
    /// <param name="executors">Registered operation executors.</param>
    /// <param name="policy">Policy decision point consulted before execution.</param>
    /// <param name="clock">Time provider used for handle id generation.</param>
    /// <param name="approvalBridge">Optional durable proposal bridge for RequireApproval decisions.</param>
    /// <param name="fallbackExecutorResolver">Optional resolver for catalog-derived executors.</param>
    public OperationDispatcher(
        IOperationCatalog catalog,
        IEnumerable<IOperationExecutor> executors,
        IOperationPolicyDecisionPoint policy,
        TimeProvider clock,
        IOperationApprovalProposalBridge? approvalBridge = null,
        Func<string, IOperationExecutor?>? fallbackExecutorResolver = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);
        _catalog = catalog;
        _policy = policy;
        _clock = clock;
        _approvalBridge = approvalBridge;
        _fallbackExecutorResolver = fallbackExecutorResolver;
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

        var descriptor = await _catalog.GetDescriptorAsync(request.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new OperationNotFoundException(request.OperationId);
        var executor = ResolveExecutor(request.OperationId);

        var decision = await _policy
            .EvaluateAsync(descriptor, request, context, cancellationToken)
            .ConfigureAwait(false);

        // Guardrail seam: anything other than Allow short-circuits the executor.
        if (decision.Kind == PolicyDecisionKind.RequireApproval && _approvalBridge is not null)
        {
            return await _approvalBridge
                .CreateProposalAsync(descriptor, request, context, decision, cancellationToken)
                .ConfigureAwait(false);
        }

        if (decision.Kind != PolicyDecisionKind.Allow)
        {
            return BuildDecisionHandle(request.OperationId, decision);
        }

        return await executor.SubmitAsync(request, context, cancellationToken).ConfigureAwait(false);
    }

    private IOperationExecutor ResolveExecutor(string operationId)
        => _executors.TryGetValue(operationId, out var executor)
            ? executor
            : _fallbackExecutorResolver?.Invoke(operationId) is { } fallback
                ? fallback
            : throw new OperationNotFoundException(operationId);

    private OperationHandle BuildDecisionHandle(string operationId, PolicyDecision decision)
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

        return new OperationHandle
        {
            OperationId = operationId,
            HandleId = NewHandleId(),
            Status = status,

            // Only RequireApproval routes to an approval lane; Deny/DryRunFirst carry none.
            ApprovalLane = decision.Kind == PolicyDecisionKind.RequireApproval
                ? decision.ApprovalLane
                : null,
            Reason = decision.Reason ?? decision.Kind.ToString()
        };
    }

    private string NewHandleId()
        => $"op-{_clock.GetUtcNow().ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}"[..32];
}
