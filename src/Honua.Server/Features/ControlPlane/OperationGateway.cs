// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane;

/// <summary>
/// Shared <see cref="IOperationGateway"/> choke point. Resolves the guardrail tier
/// for a mutating operation and routes it: blocked → returns a blocked result;
/// direct-execute → runs the registered executor; requires-approval → persists a
/// proposal, audits <c>operation.proposed</c>, and emits a pending notification
/// (#1693).
/// </summary>
internal sealed partial class OperationGateway : IOperationGateway
{
    private readonly IGuardrailLadder _ladder;
    private readonly IOperationProposalStore _proposalStore;
    private readonly Dictionary<OperationClass, IOperationExecutor> _executors;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProposalNotifier _notifier;
    private readonly ILogger<OperationGateway> _logger;

    public OperationGateway(
        IGuardrailLadder ladder,
        IOperationProposalStore proposalStore,
        IEnumerable<IOperationExecutor> executors,
        IServiceScopeFactory scopeFactory,
        IProposalNotifier notifier,
        ILogger<OperationGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _ladder = ladder;
        _proposalStore = proposalStore;
        _executors = executors.ToDictionary(executor => executor.OperationClass);
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<OperationGatewayResult> RouteAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var decision = _ladder.Resolve(request.Kind);
        Log.OperationRouted(_logger, request.Kind.ToString(), decision.Tier.ToString(), decision.Source);

        return decision.Tier switch
        {
            GuardrailTier.Blocked => new OperationGatewayResult
            {
                Outcome = OperationGatewayOutcome.Blocked,
                Decision = decision,
                Message = $"Operation '{request.Kind}' is not permitted at the {decision.Edition} edition."
            },
            GuardrailTier.RequiresApproval => await CreateProposalAsync(request, decision, cancellationToken)
                .ConfigureAwait(false),
            _ => await ExecuteDirectAsync(request, decision, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<OperationProposal?> ApplyApprovedProposalAsync(
        string proposalId,
        string approvedBy,
        CancellationToken cancellationToken = default)
    {
        var proposal = await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal == null)
        {
            return null;
        }

        if (proposal.Status != OperationProposalStatus.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"Proposal '{proposalId}' is '{proposal.Status}' and cannot be approved.");
        }

        // Atomically claim the proposal before invoking the executor.
        // Transitions AwaitingApproval → Executing via a CAS write: only one concurrent
        // caller wins this write; all others re-read a non-AwaitingApproval status and
        // throw, preventing double-execution of non-idempotent operations (BH4-031).
        proposal = await ClaimForExecutionAsync(proposal, cancellationToken).ConfigureAwait(false);

        var request = RebuildRequest(proposal);
        string? executionOperationId = null;
        var status = OperationProposalStatus.Submitted;
        string? failureMessage = null;
        var executorFound = false;

        try
        {
            if (_executors.TryGetValue(proposal.Kind, out var executor))
            {
                executorFound = true;
                executionOperationId = await executor
                    .ExecuteAsync(request, proposal.Plan.ExecutionPayload, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ProposalExecutionFailed(_logger, proposalId, ex);
            status = OperationProposalStatus.Failed;
            failureMessage = $"Execution failed ({ex.GetType().Name}).";
        }

        // BH6-033: if no executor was registered for this operation class, fail the proposal
        // with a terminal Failed status rather than persisting the non-terminal Submitted state
        // (which would leave the proposal stuck in the active index indefinitely).
        if (!executorFound && failureMessage == null)
        {
            Log.NoExecutorRegisteredForApproval(_logger, proposalId, proposal.Kind.ToString());
            status = OperationProposalStatus.Failed;
            failureMessage = $"No executor is registered for operation kind '{proposal.Kind}'; the operation was not performed.";
        }

        var now = DateTimeOffset.UtcNow;
        var resolved = proposal with
        {
            Status = status,
            ResolvedBy = approvedBy,
            ResolvedAt = now,
            ExecutionOperationId = executionOperationId,
            Plan = failureMessage == null
                ? proposal.Plan
                : proposal.Plan with { BlockingReasons = [.. proposal.Plan.BlockingReasons, failureMessage] }
        };

        await PersistResolutionAsync(resolved, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(resolved, "operation.applied", approvedBy, AuditOutcome.Success, cancellationToken)
            .ConfigureAwait(false);
        await _notifier.NotifyResolvedAsync(resolved, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    public async Task<OperationProposal?> RejectProposalAsync(
        string proposalId,
        string rejectedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        }

        var proposal = await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal == null)
        {
            return null;
        }

        if (proposal.Status != OperationProposalStatus.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"Proposal '{proposalId}' is '{proposal.Status}' and cannot be rejected.");
        }

        var now = DateTimeOffset.UtcNow;
        var resolved = proposal with
        {
            Status = OperationProposalStatus.Rejected,
            ResolvedBy = rejectedBy,
            ResolutionReason = reason,
            ResolvedAt = now
        };

        await PersistResolutionAsync(resolved, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(resolved, "operation.rejected", rejectedBy, AuditOutcome.Denied, cancellationToken)
            .ConfigureAwait(false);
        await _notifier.NotifyResolvedAsync(resolved, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private async Task<OperationGatewayResult> ExecuteDirectAsync(
        OperationGatewayRequest request,
        GuardrailDecision decision,
        CancellationToken cancellationToken)
    {
        // BH6-032: if no executor is registered for this operation class, return NotSupported
        // rather than silently claiming Executed with no work performed.
        if (!_executors.TryGetValue(request.Kind, out var executor))
        {
            Log.NoExecutorRegisteredForDirect(_logger, request.Kind.ToString());
            return new OperationGatewayResult
            {
                Outcome = OperationGatewayOutcome.NotSupported,
                Decision = decision,
                Message = $"No executor is registered for operation kind '{request.Kind}'; the operation was not performed."
            };
        }

        var executionOperationId = await executor
            .ExecuteAsync(request, request.ExecutionPayload, cancellationToken)
            .ConfigureAwait(false);

        return new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.Executed,
            Decision = decision,
            ExecutionOperationId = executionOperationId,
            Message = "Executed directly."
        };
    }

    private async Task<OperationGatewayResult> CreateProposalAsync(
        OperationGatewayRequest request,
        GuardrailDecision decision,
        CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        if (plan == null && _executors.TryGetValue(request.Kind, out var executor))
        {
            plan = await executor.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        }

        plan ??= new OperationProposalPlan { Summary = $"{request.Kind} operation" };
        if (request.ExecutionPayload != null && plan.ExecutionPayload == null)
        {
            plan = plan with { ExecutionPayload = request.ExecutionPayload };
        }

        var now = DateTimeOffset.UtcNow;
        var proposal = new OperationProposal
        {
            ProposalId = $"proposal-{Guid.NewGuid():N}",
            Kind = request.Kind,
            Status = OperationProposalStatus.AwaitingApproval,
            RequestedBy = request.RequestedBy,
            RequestedByAgent = request.RequestedByAgent,
            Plan = plan,
            GuardrailDecision = decision,
            Audit = new OperationAuditInfo
            {
                RequestedBy = request.RequestedBy,
                Reason = request.Reason,
                IdempotencyKey = request.IdempotencyKey,
                CorrelationId = request.CorrelationId,
            },
            CreatedAt = now,
            UpdatedAt = now,
        };

        var created = await _proposalStore.TryCreateAsync(proposal, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException("Failed to durably persist the operation proposal.");
        }

        await WriteAuditAsync(
                proposal,
                "operation.proposed",
                request.RequestedBy ?? request.RequestedByAgent ?? AuditEvent.AnonymousActor,
                AuditOutcome.Success,
                cancellationToken)
            .ConfigureAwait(false);
        await _notifier.NotifyPendingAsync(proposal, cancellationToken).ConfigureAwait(false);

        return new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = decision,
            ProposalId = proposal.ProposalId,
            Message = "Proposal created and awaiting approval."
        };
    }

    private async Task PersistResolutionAsync(OperationProposal proposal, CancellationToken cancellationToken)
    {
        // Refresh-and-retry on optimistic version conflict so concurrent
        // notifications/reconcilers do not lose the resolution write.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await _proposalStore.TrySetAsync(proposal, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var latest = await _proposalStore.GetAsync(proposal.ProposalId, cancellationToken).ConfigureAwait(false);
            if (latest == null)
            {
                throw new InvalidOperationException($"Proposal '{proposal.ProposalId}' disappeared during resolution.");
            }

            // Do not overwrite a terminal resolution. If an operator resolved the proposal
            // concurrently (or a previous write landed between the CAS failure and this
            // re-read), stop retrying rather than risk overwriting the recorded resolution.
            if (IsTerminalStatus(latest.Status))
            {
                throw new InvalidOperationException(
                    $"Proposal '{proposal.ProposalId}' reached terminal status '{latest.Status}' " +
                    "before the resolution write landed; aborting to avoid overwriting it.");
            }

            // Only the version was bumped (e.g. a notification write); the status is still
            // active so it is safe to retry with the refreshed version.
            proposal = proposal with { Version = latest.Version };
        }

        throw new InvalidOperationException(
            $"Failed to persist resolution for proposal '{proposal.ProposalId}' after repeated version conflicts.");
    }

    /// <summary>
    /// Atomically transitions a proposal from <see cref="OperationProposalStatus.AwaitingApproval"/>
    /// to <see cref="OperationProposalStatus.Executing"/> via a CAS write.
    /// Returns the updated proposal (with its incremented version token) on success.
    /// Throws when another caller already claimed or resolved the proposal, or when the
    /// claim cannot be won after retries due to persistent version conflicts.
    /// </summary>
    private async Task<OperationProposal> ClaimForExecutionAsync(
        OperationProposal proposal,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var claiming = proposal with { Status = OperationProposalStatus.Executing };
            if (await _proposalStore.TrySetAsync(claiming, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
            {
                // TrySetAsync stores version+1 internally; return a record whose Version
                // reflects the new stored value so PersistResolutionAsync uses the correct
                // token for its subsequent CAS write.
                return claiming with { Version = proposal.Version + 1 };
            }

            // CAS failed — re-read to find out why.
            var latest = await _proposalStore.GetAsync(proposal.ProposalId, cancellationToken)
                .ConfigureAwait(false);
            if (latest == null)
            {
                throw new InvalidOperationException(
                    $"Proposal '{proposal.ProposalId}' disappeared while claiming for execution.");
            }

            if (latest.Status != OperationProposalStatus.AwaitingApproval)
            {
                // Another concurrent caller already claimed (Executing) or fully resolved
                // this proposal. Do not proceed to execute the operation a second time.
                throw new InvalidOperationException(
                    $"Proposal '{proposal.ProposalId}' is '{latest.Status}' — it was claimed or " +
                    "resolved concurrently; this call will not execute the operation again.");
            }

            // Status is still AwaitingApproval but the version advanced (e.g. a
            // notification write bumped the record). Refresh the version and retry.
            proposal = latest;
        }

        throw new InvalidOperationException(
            $"Failed to claim proposal '{proposal.ProposalId}' for execution after repeated version conflicts.");
    }

    private static bool IsTerminalStatus(OperationProposalStatus status)
        => status is OperationProposalStatus.Succeeded
            or OperationProposalStatus.Failed
            or OperationProposalStatus.Rejected
            or OperationProposalStatus.RolledBack;

    private static OperationGatewayRequest RebuildRequest(OperationProposal proposal) => new()
    {
        Kind = proposal.Kind,
        RequestedBy = proposal.RequestedBy,
        RequestedByAgent = proposal.RequestedByAgent,
        Reason = proposal.Audit.Reason,
        CorrelationId = proposal.Audit.CorrelationId,
        IdempotencyKey = proposal.Audit.IdempotencyKey,
        Plan = proposal.Plan,
        ExecutionPayload = proposal.Plan.ExecutionPayload,
    };

    // The gateway is a singleton but IAuditLog is scoped (PostgresAuditLog needs a
    // per-operation DB connection), so resolve it from a fresh scope per audit write
    // rather than capturing it as a constructor dependency. Capturing the scoped
    // service would be a captive dependency and fails DI scope validation at startup
    // under the durable control-plane path (honua-server#1908).
    private async Task WriteAuditAsync(
        OperationProposal proposal,
        string action,
        string actor,
        AuditOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        await auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.AdminAction,
                Actor = string.IsNullOrWhiteSpace(actor) ? AuditEvent.AnonymousActor : actor,
                ActorType = AuditActorType.UserId,
                ResourceType = "operation_proposal",
                ResourceId = proposal.ProposalId,
                Action = action,
                Outcome = outcome,
                CorrelationId = proposal.Audit.CorrelationId ?? proposal.ProposalId,
                Details = $"{{\"kind\":\"{proposal.Kind}\",\"status\":\"{proposal.Status}\"}}",
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(9200, LogLevel.Information, "Routed {Kind} operation to tier {Tier} ({Source})")]
        public static partial void OperationRouted(ILogger logger, string kind, string tier, string source);

        [LoggerMessage(9201, LogLevel.Error, "Execution of approved proposal {ProposalId} failed")]
        public static partial void ProposalExecutionFailed(ILogger logger, string proposalId, Exception exception);

        [LoggerMessage(9202, LogLevel.Warning, "No executor registered for direct-execute of operation kind {Kind}; returning NotSupported")]
        public static partial void NoExecutorRegisteredForDirect(ILogger logger, string kind);

        [LoggerMessage(9203, LogLevel.Warning, "No executor registered for approval of proposal {ProposalId} with kind {Kind}; failing proposal to terminal Failed")]
        public static partial void NoExecutorRegisteredForApproval(ILogger logger, string proposalId, string kind);
    }
}
