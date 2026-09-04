// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Default <see cref="IOperationInvoker"/>. Resolves the descriptor + executor for an
/// operation id, runs the policy decision point, and only on
/// <see cref="PolicyDecisionKind.Allow"/> calls the executor. For Deny / RequireApproval /
/// DryRunFirst it returns a handle that reflects the decision WITHOUT touching the executor —
/// proving the guardrail seam holds for every registered policy implementation.
/// </summary>
public sealed class OperationDispatcher : IOperationInvoker
{
    private static readonly TimeSpan PostActuationPersistenceTimeout = TimeSpan.FromSeconds(10);

    private readonly IOperationCatalog _catalog;
    private readonly Dictionary<string, IOperationExecutor> _executors;
    private readonly IOperationPolicyDecisionPoint _policy;
    private readonly IOperationApprovalBridge? _approvalBridge;
    private readonly IOperationInstanceStore _instanceStore;
    private readonly IAuditLog _auditLog;
    private readonly OperationEnvelopeFactory _envelopeFactory;
    private readonly IOperationApprovalReplayVerifier? _approvalReplayVerifier;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of <see cref="OperationDispatcher"/>.
    /// </summary>
    /// <param name="catalog">Operation grounding catalog.</param>
    /// <param name="executors">Registered operation executors.</param>
    /// <param name="policy">Policy decision point consulted before execution.</param>
    /// <param name="clock">Time provider used for envelope timestamps.</param>
    /// <param name="approvalBridge">Optional durable approval persistence seam.</param>
    /// <param name="instanceStore">Operation-instance store. Omit only in explicit tests.</param>
    /// <param name="auditLog">Audit sink. Omit only in explicit tests.</param>
    /// <param name="approvalReplayVerifier">Durable approved-replay authority verifier.</param>
    public OperationDispatcher(
        IOperationCatalog catalog,
        IEnumerable<IOperationExecutor> executors,
        IOperationPolicyDecisionPoint policy,
        TimeProvider clock,
        IOperationApprovalBridge? approvalBridge = null,
        IOperationInstanceStore? instanceStore = null,
        IAuditLog? auditLog = null,
        IOperationApprovalReplayVerifier? approvalReplayVerifier = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);
        _catalog = catalog;
        _policy = policy;
        _clock = clock;
        _approvalBridge = approvalBridge;
        _instanceStore = instanceStore ?? new VolatileOperationInstanceStore();
        _auditLog = auditLog ?? new VolatileOperationAuditLog();
        _envelopeFactory = new OperationEnvelopeFactory(_instanceStore, _auditLog, clock);
        _approvalReplayVerifier = approvalReplayVerifier;
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

        // Resolve both halves of the runtime contract before accepting an envelope. A
        // descriptor can remain discoverable while its actuator is deliberately withheld;
        // such a submission must not create a handle or approval proposal.
        var descriptor = await _catalog.GetDescriptorAsync(request.OperationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationNotFoundException(request.OperationId);
        var executor = ResolveExecutor(request.OperationId);

        var createdAt = _clock.GetUtcNow();
        OperationHandle envelope;
        var invocationContext = context;

        string? acceptanceAuditId;
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId))
        {
            if (context.ScopeGoverned &&
                (context.RecognizedScopes.Count == 0 ||
                 context.RecognizedScopes.Any(scope =>
                     !OperatorScopeCatalog.SupportedScopes.Contains(scope, StringComparer.Ordinal)) ||
                 !OperationScopeMapping.TryResolve(request, out var requiredOperation) ||
                 !OperatorScopeCatalog.PermitsOperation(
                     context.RecognizedScopes.ToHashSet(StringComparer.Ordinal), requiredOperation)))
            {
                return new OperationHandle
                {
                    OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
                    OperationId = request.OperationId,
                    CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
                    Status = OperationHandleStatus.Failed,
                    CreatedAt = createdAt,
                    UpdatedAt = _clock.GetUtcNow(),
                    Reason = "Approved replay operation exceeds the sealed OAuth scope authority.",
                };
            }

            if (string.IsNullOrWhiteSpace(context.OperationInstanceId) ||
                string.IsNullOrWhiteSpace(context.ApprovedPlanHash) ||
                _approvalReplayVerifier is null ||
                !await _approvalReplayVerifier.VerifyAsync(
                        context.ApprovedProposalId,
                        context.OperationInstanceId,
                        context.ApprovedPlanHash,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return new OperationHandle
                {
                    OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
                    OperationId = request.OperationId,
                    CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
                    Status = OperationHandleStatus.Failed,
                    CreatedAt = createdAt,
                    UpdatedAt = _clock.GetUtcNow(),
                    Reason = "Approved replay proof did not match the durable proposal authority.",
                };
            }

            var existing = await _instanceStore
                .GetAsync(context.OperationInstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null || string.IsNullOrWhiteSpace(existing.AuditId))
            {
                return new OperationHandle
                {
                    OperationInstanceId = context.OperationInstanceId,
                    OperationId = request.OperationId,
                    CorrelationId = string.IsNullOrWhiteSpace(context.CorrelationId)
                        ? $"corr-{Guid.NewGuid():N}"
                        : context.CorrelationId,
                    Status = OperationHandleStatus.Failed,
                    CreatedAt = createdAt,
                    UpdatedAt = _clock.GetUtcNow(),
                    Reason = "Approved replay could not resolve the original durable operation instance.",
                };
            }

            acceptanceAuditId = existing.AuditId;
            envelope = existing with
            {
                Status = OperationHandleStatus.Accepted,
                UpdatedAt = _clock.GetUtcNow(),
                AuthorizationOutcome = invocationContext.AuthorizationOutcome,
            };
            if (!await _instanceStore.TrySetAsync(envelope, existing.Version, cancellationToken).ConfigureAwait(false))
            {
                return existing with
                {
                    Status = OperationHandleStatus.Failed,
                    UpdatedAt = _clock.GetUtcNow(),
                    Reason = "Approved replay could not claim the original operation instance due to a concurrent transition.",
                };
            }
            invocationContext = context with
            {
                OperationInstanceId = envelope.OperationInstanceId,
                CorrelationId = envelope.CorrelationId,
                AuditId = acceptanceAuditId,
                ProposalId = context.ApprovedProposalId,
            };
            createdAt = envelope.CreatedAt;
        }
        else
        {
            envelope = await _envelopeFactory
                .CreateAcceptedAsync(request.OperationId, context, cancellationToken)
                .ConfigureAwait(false);
            // A durable idempotency lookup may return the already-routed invocation. Only a
            // newly accepted (or explicitly reset pre-actuation cancellation) envelope may
            // proceed to validation/policy/actuation; the retry touch audit was already written
            // by the factory.
            if (envelope.Status != OperationHandleStatus.Accepted)
            {
                return envelope;
            }

            invocationContext = context with
            {
                OperationInstanceId = envelope.OperationInstanceId,
                CorrelationId = envelope.CorrelationId,
                AuditId = envelope.AuditId,
                ProposalId = envelope.ProposalId,
            };
            acceptanceAuditId = envelope.AuditId;
            createdAt = envelope.CreatedAt;
        }

        var operationInstanceId = envelope.OperationInstanceId;
        var correlationId = envelope.CorrelationId;

        OperationValidation validation;
        try
        {
            validation = await executor.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PersistPreActuationCancellationAsync(envelope).ConfigureAwait(false);
            throw;
        }
        catch (OperationNotFoundException)
        {
            return await PersistFailureAsync(
                    envelope,
                    $"No executor is registered for operation '{request.OperationId}'.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await PersistFailureAsync(
                    envelope,
                    $"Operation validation failed ({ex.GetType().Name}).",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!validation.IsValid)
        {
            return await PersistFailureAsync(
                    envelope,
                    string.Join(" ", validation.Messages),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        PolicyDecision decision;
        try
        {
            decision = string.IsNullOrWhiteSpace(invocationContext.ApprovedProposalId)
                ? await _policy
                    .EvaluateAsync(descriptor, request, invocationContext, cancellationToken)
                    .ConfigureAwait(false)
                : PolicyDecision.Allowed;
            envelope = envelope with
            {
                OperationId = descriptor.OperationId,
                PolicyDecision = decision.Kind,
                UpdatedAt = _clock.GetUtcNow(),
            };
            await _instanceStore.SetAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PersistPreActuationCancellationAsync(envelope).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await PersistFailureAsync(
                    envelope,
                    $"Operation policy evaluation or evidence persistence failed ({ex.GetType().Name}).",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (decision.Kind != PolicyDecisionKind.Allow)
        {
            try
            {
                var decided = await BuildDecisionHandleAsync(
                        descriptor,
                        validation.ApprovalPlan is null || request.GatewayRequest is null
                            ? request
                            : request with
                            {
                                GatewayRequest = request.GatewayRequest with
                                {
                                    Plan = validation.ApprovalPlan,
                                    ExecutionPayload = validation.ApprovalPlan.ExecutionPayload
                                        ?? request.GatewayRequest.ExecutionPayload,
                                },
                            },
                        invocationContext,
                        decision,
                        createdAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                var projected = decided with { AuditId = decided.AuditId ?? acceptanceAuditId };
                return projected.Status == OperationHandleStatus.RequiresApproval
                    ? await PersistApprovalDecisionAsync(projected, cancellationToken).ConfigureAwait(false)
                    : await PersistAsync(projected, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await PersistPreActuationCancellationAsync(envelope).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return await PersistFailureAsync(
                        envelope,
                        $"Operation decision routing failed ({ex.GetType().Name}).",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        OperationHandle executed;
        if (request.DryRun && !descriptor.Policy.SupportsDryRun)
        {
            return await PersistFailureAsync(
                    envelope,
                    $"Operation '{descriptor.OperationId}' does not support dry-run execution.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.DryRun)
        {
            executed = envelope with
            {
                Status = OperationHandleStatus.Completed,
                UpdatedAt = _clock.GetUtcNow(),
                Reason = "Dry run completed; no actuator was invoked.",
                Result = new OperationResultSummary
                {
                    Summary = "Operation validation completed without actuation.",
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["dryRun"] = bool.TrueString,
                        ["validationStatus"] = validation.Status,
                    },
                },
            };
        }
        else
        {
            try
            {
                executed = await executor.SubmitAsync(request, invocationContext, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                executed = envelope with
                {
                    Status = OperationHandleStatus.Indeterminate,
                    UpdatedAt = _clock.GetUtcNow(),
                    Reason = "Actuation was canceled after it began; side effects may have committed.",
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return await PersistFailureAsync(
                        envelope,
                        $"Operation actuation failed ({ex.GetType().Name}).",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var completed = executed with
        {
            OperationInstanceId = operationInstanceId,
            OperationId = descriptor.OperationId,
            CorrelationId = correlationId,
            AuditId = acceptanceAuditId,
            CreatedAt = createdAt,
            UpdatedAt = _clock.GetUtcNow(),
            AuthorizationOutcome = invocationContext.AuthorizationOutcome,
            PolicyDecision = PolicyDecisionKind.Allow,
        };
        string? terminalAuditId;
        using var terminalAuditTimeout = new CancellationTokenSource(PostActuationPersistenceTimeout);
        try
        {
            var auditAction = completed.Status is OperationHandleStatus.Queued or OperationHandleStatus.Running
                ? "operation.submitted"
                : "operation.completed";
            var auditOutcome = completed.Status is OperationHandleStatus.Completed
                or OperationHandleStatus.Queued
                or OperationHandleStatus.Running
                    ? AuditOutcome.Success
                    : AuditOutcome.Failure;
            terminalAuditId = await WriteAuditAsync(
                    completed,
                    invocationContext,
                    auditAction,
                    auditOutcome,
                    terminalAuditTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            terminalAuditId = null;
            completed = completed with
            {
                Reason = "Actuation returned, but terminal audit evidence timed out.",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            terminalAuditId = null;
            completed = completed with
            {
                Reason = $"Actuation returned, but terminal audit evidence failed ({ex.GetType().Name}).",
            };
        }
        if (string.IsNullOrWhiteSpace(terminalAuditId))
        {
            completed = completed with
            {
                Status = OperationHandleStatus.Indeterminate,
                Reason = "Actuation returned, but terminal audit evidence could not be persisted.",
                UpdatedAt = _clock.GetUtcNow(),
            };
        }

        using var terminalPersistenceTimeout = new CancellationTokenSource(PostActuationPersistenceTimeout);
        try
        {
            return await PersistAsync(completed, terminalPersistenceTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return completed with
            {
                Status = OperationHandleStatus.Indeterminate,
                UpdatedAt = _clock.GetUtcNow(),
                Reason = "Actuation returned, but terminal envelope persistence timed out.",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return completed with
            {
                Status = OperationHandleStatus.Indeterminate,
                UpdatedAt = _clock.GetUtcNow(),
                Reason = $"Actuation returned, but the terminal envelope could not be persisted ({ex.GetType().Name}).",
            };
        }
    }

    private async Task<OperationHandle> PersistFailureAsync(
        OperationHandle envelope,
        string reason,
        CancellationToken cancellationToken)
    {
        var failed = envelope with
        {
            Status = OperationHandleStatus.Failed,
            UpdatedAt = _clock.GetUtcNow(),
            Reason = reason,
        };
        try
        {
            await _instanceStore.SetAsync(failed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return failed with
            {
                Reason = $"{reason} The failure envelope could not be durably persisted ({ex.GetType().Name}).",
            };
        }

        return failed;
    }

    private async Task PersistPreActuationCancellationAsync(OperationHandle envelope)
    {
        var cancelled = envelope with
        {
            Status = OperationHandleStatus.Cancelled,
            UpdatedAt = _clock.GetUtcNow(),
            Reason = "Operation was canceled before actuation began; no side effect occurred.",
        };
        using var timeout = new CancellationTokenSource(PostActuationPersistenceTimeout);
        try
        {
            var auditId = await WriteAuditAsync(
                    cancelled,
                    new OperationPolicyContext
                    {
                        OperationInstanceId = cancelled.OperationInstanceId,
                        CorrelationId = cancelled.CorrelationId,
                        AuthorizationOutcome = cancelled.AuthorizationOutcome,
                    },
                    "operation.cancelled",
                    AuditOutcome.Failure,
                    timeout.Token)
                .ConfigureAwait(false);
            await _instanceStore.SetAsync(
                    cancelled with { AuditId = auditId ?? cancelled.AuditId },
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cancellation must still propagate. The bounded compensation is best-effort when
            // durable infrastructure itself is unavailable and cannot extend request lifetime.
        }
    }

    private async Task<OperationHandle> PersistAsync(
        OperationHandle envelope,
        CancellationToken cancellationToken)
    {
        await _instanceStore.SetAsync(envelope, cancellationToken).ConfigureAwait(false);
        return await _instanceStore.GetAsync(envelope.OperationInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The persisted operation instance could not be reloaded.");
    }

    private async Task<OperationHandle> PersistApprovalDecisionAsync(
        OperationHandle approval,
        CancellationToken cancellationToken)
    {
        var current = await _instanceStore.GetAsync(approval.OperationInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The accepted operation instance disappeared before approval persistence.");
        if (IsTerminal(current.Status))
        {
            return current;
        }

        if (await _instanceStore.TrySetAsync(approval, current.Version, cancellationToken).ConfigureAwait(false))
        {
            return approval with { Version = current.Version + 1 };
        }

        var winner = await _instanceStore.GetAsync(approval.OperationInstanceId, cancellationToken).ConfigureAwait(false);
        if (winner is not null && IsTerminal(winner.Status))
        {
            return winner;
        }

        throw new InvalidOperationException(
            "The approval transition was refused because the operation instance version changed.");
    }

    private static bool IsTerminal(OperationHandleStatus status)
        => status is OperationHandleStatus.Completed
            or OperationHandleStatus.Denied
            or OperationHandleStatus.DryRunRequired
            or OperationHandleStatus.Rejected
            or OperationHandleStatus.Cancelled
            or OperationHandleStatus.Failed
            or OperationHandleStatus.Indeterminate;

    private Task<string?> WriteAuditAsync(
        OperationHandle envelope,
        OperationPolicyContext context,
        string action,
        AuditOutcome outcome,
        CancellationToken cancellationToken)
        => _auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = _clock.GetUtcNow(),
            EventType = AuditEventType.AdminAction,
            Actor = context.PrincipalId ?? AuditEvent.AnonymousActor,
            ActorType = context.PrincipalId is null ? AuditActorType.Anonymous : AuditActorType.UserId,
            ResourceType = "operation_instance",
            ResourceId = envelope.OperationInstanceId,
            Action = action,
            Outcome = outcome,
            CorrelationId = envelope.CorrelationId,
            Details = $"operationId={envelope.OperationId};status={envelope.Status}",
        }, cancellationToken);

    private IOperationExecutor ResolveExecutor(string operationId)
        => _executors.TryGetValue(operationId, out var executor)
            ? executor
            : AdminMcpOperationExclusions.RequiresSecretAwareRuntime(operationId)
                ? throw new OperationUnavailableException(
                    operationId,
                    $"Operation '{operationId}' is unavailable through the operations runtime until issue #4187 lands.")
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
