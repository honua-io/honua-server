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

    /// <summary>
    /// Initializes a new instance of <see cref="OperationDispatcher"/>.
    /// </summary>
    /// <param name="catalog">Operation grounding catalog.</param>
    /// <param name="executors">Registered operation executors.</param>
    /// <param name="policy">Policy decision point consulted before execution.</param>
    /// <param name="clock">Time provider used for handle id generation.</param>
    public OperationDispatcher(
        IOperationCatalog catalog,
        IEnumerable<IOperationExecutor> executors,
        IOperationPolicyDecisionPoint policy,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);
        _catalog = catalog;
        _policy = policy;
        _clock = clock;
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
        if (decision.Kind != PolicyDecisionKind.Allow)
        {
            return BuildDecisionHandle(request.OperationId, decision);
        }

        return await executor.SubmitAsync(request, context, cancellationToken).ConfigureAwait(false);
    }

    private IOperationExecutor ResolveExecutor(string operationId)
        => _executors.TryGetValue(operationId, out var executor)
            ? executor
            : throw new OperationNotFoundException(operationId);

    private OperationHandle BuildDecisionHandle(string operationId, PolicyDecision decision)
    {
        var status = decision.Kind switch
        {
            PolicyDecisionKind.RequireApproval => OperationHandleStatus.RequiresApproval,
            PolicyDecisionKind.DryRunFirst => OperationHandleStatus.RequiresApproval,
            _ => OperationHandleStatus.Failed
        };

        return new OperationHandle
        {
            OperationId = operationId,
            HandleId = NewHandleId(),
            Status = status,
            ApprovalLane = decision.ApprovalLane,
            Reason = decision.Reason ?? decision.Kind.ToString()
        };
    }

    private string NewHandleId()
        => $"op-{_clock.GetUtcNow().ToUnixTimeMilliseconds():x}-{Guid.NewGuid():N}"[..32];
}
