// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
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

        // For an AdminConfigChange, the ops-action discriminator selects a per-action
        // guardrail tier. Prefer an explicit discriminator on the request; otherwise
        // derive it from the {action,...} execution payload so any ops-action routed
        // through the gateway is guardrail-gated (unknown actions fail closed to Blocked).
        var actionDiscriminator = request.ActionDiscriminator
            ?? (request.Kind == OperationClass.AdminConfigChange
                ? TryReadActionDiscriminator(request.ExecutionPayload)
                : null);

        // Undiscriminated requests resolve through the classic class-only overload so
        // their behavior (and existing ladder implementations) is unchanged.
        var decision = actionDiscriminator is null
            ? _ladder.Resolve(request.Kind)
            : _ladder.Resolve(request.Kind, actionDiscriminator);
        Log.OperationRouted(_logger, request.Kind.ToString(), decision.Tier.ToString(), decision.Source);

        if (decision.Tier == GuardrailTier.RequiresApproval)
        {
            var autonomy = await EvaluateAutonomyAsync(
                    request,
                    decision,
                    actionDiscriminator,
                    cancellationToken)
                .ConfigureAwait(false);
            if (autonomy.ShouldAutoApply)
            {
                return await ExecuteAutonomousAsync(request, autonomy, cancellationToken).ConfigureAwait(false);
            }
        }

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

    public async Task<OperationGatewayResult> CreateApprovalProposalAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The approval requirement was already decided by an upstream domain gate
        // (e.g. the geoprocessing destructive-plan gate), so we do NOT re-run the
        // edition ladder — we only need the RequiresApproval floor. Resolve the class
        // decision to carry a truthful edition/source, then force the tier to
        // RequiresApproval so the proposal is always persisted for human resolution.
        var baseDecision = _ladder.Resolve(request.Kind);
        var decision = baseDecision.Tier == GuardrailTier.RequiresApproval
            ? baseDecision
            : new GuardrailDecision(
                GuardrailTier.RequiresApproval,
                request.Kind,
                baseDecision.Edition,
                "upstream-gate-requires-approval");

        Log.OperationRouted(_logger, request.Kind.ToString(), decision.Tier.ToString(), decision.Source);

        return await CreateProposalAsync(request, decision, cancellationToken).ConfigureAwait(false);
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
            await ReconcileAutonomyProposalResolutionAsync(proposal, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: this is the operation-proposal execution boundary.
            // An executor failure must terminate this proposal with a Failed status rather
            // than propagate and leave the proposal stuck in a non-terminal state.
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
        await RecordAutonomyProposalResolutionAsync(
                resolved,
                OpsAutonomyProposalResolution.Approved,
                cancellationToken)
            .ConfigureAwait(false);
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
            await ReconcileAutonomyProposalResolutionAsync(proposal, cancellationToken).ConfigureAwait(false);
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
        await RecordAutonomyProposalResolutionAsync(
                resolved,
                OpsAutonomyProposalResolution.Rejected,
                cancellationToken)
            .ConfigureAwait(false);
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

        // Honor the idempotency key on the approval path: a repeated proposal (e.g.
        // propose → refresh → propose) must fold onto the existing active proposal
        // rather than minting a duplicate AwaitingApproval record (#1693). The
        // proposal id is derived deterministically from the key so a concurrent
        // duplicate TryCreate collides and we fetch-and-return the winner (race-safe).
        var hasIdempotencyKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey);
        if (hasIdempotencyKey)
        {
            var existing = await FindActiveByIdempotencyKeyAsync(request.Kind, request.IdempotencyKey!, cancellationToken)
                .ConfigureAwait(false);
            if (existing != null)
            {
                return ExistingProposalResult(existing, decision, request.IdempotencyKey!);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var proposal = new OperationProposal
        {
            ProposalId = hasIdempotencyKey
                ? DeriveProposalId(request.Kind, request.IdempotencyKey!)
                : $"proposal-{Guid.NewGuid():N}",
            Kind = request.Kind,
            Status = OperationProposalStatus.AwaitingApproval,
            RequestedBy = request.RequestedBy,
            RequestedByAgent = request.RequestedByAgent,
            Plan = plan,
            GuardrailDecision = decision,
            AutonomyMetadata = NormalizeAutonomyContext(request.AutonomyContext, actionDiscriminator: request.ActionDiscriminator),
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
            // Lost a race (or an idempotent replay landed) under the same id: fetch and
            // return the winning proposal instead of failing the caller.
            if (hasIdempotencyKey)
            {
                var winner = await _proposalStore.GetAsync(proposal.ProposalId, cancellationToken).ConfigureAwait(false);
                if (winner != null)
                {
                    return ExistingProposalResult(winner, decision, request.IdempotencyKey!);
                }
            }

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
        await RecordAutonomyProposalRaisedAsync(request, cancellationToken).ConfigureAwait(false);

        return new OperationGatewayResult
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = decision,
            ProposalId = proposal.ProposalId,
            Message = "Proposal created and awaiting approval."
        };
    }

    private async Task<OperationProposal?> FindActiveByIdempotencyKeyAsync(
        OperationClass kind,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var active = await _proposalStore.ListActiveAsync(kind, cancellationToken).ConfigureAwait(false);
        return active.FirstOrDefault(
            proposal => string.Equals(proposal.Audit.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
    }

    private static OperationGatewayResult ExistingProposalResult(
        OperationProposal existing,
        GuardrailDecision decision,
        string idempotencyKey) => new()
        {
            Outcome = OperationGatewayOutcome.ProposalCreated,
            Decision = decision,
            ProposalId = existing.ProposalId,
            Message = $"Existing proposal returned for idempotency key '{idempotencyKey}'.",
        };

    // Derive a stable proposal id from (kind, idempotency key) so a repeated proposal
    // maps to the same durable record. This makes TryCreate collide on a duplicate,
    // giving the gateway a race-safe fetch-and-return instead of a second proposal.
    private static string DeriveProposalId(OperationClass kind, string idempotencyKey)
    {
        var material = System.Text.Encoding.UTF8.GetBytes($"{kind}:{idempotencyKey}");
        var hash = System.Security.Cryptography.SHA256.HashData(material);
        return $"proposal-{Convert.ToHexString(hash)[..32].ToLowerInvariant()}";
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

    // Best-effort read of the ops-action name from an AdminConfigChange execution
    // payload ({action, target, params}). Returns null for absent/blank/malformed
    // payloads; a malformed payload then resolves to the base tier and the real applier
    // fails it closed (never partial application).
    private static string? TryReadActionDiscriminator(string? executionPayload)
    {
        if (string.IsNullOrWhiteSpace(executionPayload))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(executionPayload);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var name = action.GetString();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed payload — leave the discriminator unset; the applier fails closed.
        }

        return null;
    }

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
        ActionDiscriminator = proposal.AutonomyMetadata?.ActionDiscriminator,
        AutonomyContext = proposal.AutonomyMetadata is null
            ? null
            : new OperationGatewayAutonomyContext
            {
                FindingId = proposal.AutonomyMetadata.FindingId,
                Rule = proposal.AutonomyMetadata.Rule,
                ActionDiscriminator = proposal.AutonomyMetadata.ActionDiscriminator,
                ActionMarkedAutoSafe = proposal.AutonomyMetadata.ActionMarkedAutoSafe,
                BlastRadius = proposal.AutonomyMetadata.BlastRadius,
                EvidenceRefs = proposal.AutonomyMetadata.EvidenceRefs,
            },
    };

    private async Task<OpsAutonomyRouteDecision> EvaluateAutonomyAsync(
        OperationGatewayRequest request,
        GuardrailDecision decision,
        string? actionDiscriminator,
        CancellationToken cancellationToken)
    {
        if (request.AutonomyContext is null)
        {
            return new OpsAutonomyRouteDecision { ShouldAutoApply = false, Reason = "no-autonomy-context" };
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var evaluator = scope.ServiceProvider.GetService<IOpsAutonomyEvaluator>();
        if (evaluator is null)
        {
            return new OpsAutonomyRouteDecision { ShouldAutoApply = false, Reason = "autonomy-evaluator-unavailable" };
        }

        return await evaluator
            .EvaluateRouteAsync(request, decision, actionDiscriminator, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OperationGatewayResult> ExecuteAutonomousAsync(
        OperationGatewayRequest request,
        OpsAutonomyRouteDecision autonomy,
        CancellationToken cancellationToken)
    {
        var directDecision = autonomy.Decision
            ?? throw new InvalidOperationException("Autonomy route decision did not include a direct-execute guardrail decision.");

        await using var convergenceScope = _scopeFactory.CreateAsyncScope();
        var convergence = convergenceScope.ServiceProvider
            .GetServices<IAutonomousOperationConvergence>()
            .FirstOrDefault(candidate => candidate.CanHandle(request));
        if (convergence is null)
        {
            var message = $"No post-action convergence verifier is registered for autonomous action "
                + $"'{request.ActionDiscriminator ?? request.Kind.ToString()}'; the action was not executed; manual intervention required.";
            return await CompleteAutonomyAsync(
                    request,
                    autonomy,
                    directDecision,
                    OperationGatewayOutcome.Failed,
                    OpsAutonomyActionOutcome.Failed,
                    operationId: null,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        OperationGatewayResult execution;
        try
        {
            execution = await ExecuteDirectAsync(
                    request,
                    directDecision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RecordPostInvocationCancellationAsync(
                    request,
                    autonomy,
                    operationId: null,
                    "Autonomous execution was canceled after actuator invocation; the underlying state is indeterminate; manual intervention required.")
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Autonomous execution failed ({ex.GetType().Name}).";
            await CompleteAutonomyAsync(
                    request,
                    autonomy,
                    directDecision,
                    OperationGatewayOutcome.Failed,
                    OpsAutonomyActionOutcome.Failed,
                    operationId: null,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        if (execution.Outcome != OperationGatewayOutcome.Executed)
        {
            return await CompleteAutonomyAsync(
                    request,
                    autonomy,
                    directDecision,
                    execution.Outcome,
                    OpsAutonomyActionOutcome.Failed,
                    execution.ExecutionOperationId,
                    execution.Message ?? "The autonomous actuator did not execute.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteAutonomyAuditAsync(
                request,
                "operation.auto_executed",
                AuditOutcome.Success,
                execution.Message,
                execution.ExecutionOperationId,
                cancellationToken)
            .ConfigureAwait(false);

        AutonomousVerificationResult verification;
        try
        {
            verification = await convergence
                .VerifyAsync(request, execution.ExecutionOperationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RecordPostInvocationCancellationAsync(
                    request,
                    autonomy,
                    execution.ExecutionOperationId,
                    "Post-action verification was canceled after execution; the underlying state is indeterminate; manual intervention required.")
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: a failure verifying the post-action state must not
            // propagate and abort autonomy handling; it is mapped to an Indeterminate
            // verification result so the caller routes to manual intervention below.
            verification = new AutonomousVerificationResult(
                AutonomousVerificationState.Indeterminate,
                $"Post-action verification could not complete ({ex.GetType().Name}).");
        }

        await WriteAutonomyAuditAsync(
                request,
                "operation.auto_verified",
                verification.State == AutonomousVerificationState.Converged
                    ? AuditOutcome.Success
                    : AuditOutcome.Failure,
                verification.Message,
                execution.ExecutionOperationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (verification.State == AutonomousVerificationState.Converged)
        {
            var message = $"Auto-applied by autonomy policy for rule '{request.AutonomyContext?.Rule}'; "
                + $"convergence verified: {verification.Message}";
            return await CompleteAutonomyAsync(
                    request,
                    autonomy,
                    directDecision,
                    OperationGatewayOutcome.Executed,
                    OpsAutonomyActionOutcome.Succeeded,
                    execution.ExecutionOperationId,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!convergence.SupportsCompensation(request))
        {
            var gatewayOutcome = verification.State == AutonomousVerificationState.Indeterminate
                ? OperationGatewayOutcome.Indeterminate
                : OperationGatewayOutcome.Failed;
            var autonomyOutcome = verification.State == AutonomousVerificationState.Indeterminate
                ? OpsAutonomyActionOutcome.Indeterminate
                : OpsAutonomyActionOutcome.Failed;
            var message = $"Post-action verification {DescribeVerification(verification.State)}: {verification.Message}; "
                + $"compensation is not supported for action '{request.ActionDiscriminator ?? request.Kind.ToString()}'; "
                + "manual intervention required.";
            return await CompleteAutonomyAsync(
                    request,
                    autonomy,
                    directDecision,
                    gatewayOutcome,
                    autonomyOutcome,
                    execution.ExecutionOperationId,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        AutonomousCompensationResult compensation;
        try
        {
            compensation = await convergence
                .CompensateAsync(request, execution.ExecutionOperationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var message = $"Post-action verification {DescribeVerification(verification.State)}: {verification.Message}; "
                + "compensation was canceled and the underlying state is indeterminate; manual intervention required.";
            await RecordPostInvocationCancellationAsync(
                    request,
                    autonomy,
                    execution.ExecutionOperationId,
                    message)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: a failure running compensation must not propagate and
            // abort autonomy handling; it is mapped to an Indeterminate compensation result
            // so the caller routes to manual intervention below.
            compensation = new AutonomousCompensationResult(
                AutonomousCompensationState.Indeterminate,
                $"Compensation could not complete ({ex.GetType().Name}).");
        }

        await WriteAutonomyAuditAsync(
                request,
                "operation.auto_compensated",
                compensation.State == AutonomousCompensationState.RolledBack
                    ? AuditOutcome.Success
                    : AuditOutcome.Failure,
                compensation.Message,
                execution.ExecutionOperationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (compensation.State == AutonomousCompensationState.RolledBack)
        {
            var message = $"Post-action verification {DescribeVerification(verification.State)}: {verification.Message}; "
                + $"compensation succeeded: {compensation.Message}";
            return await CompleteAutonomyAsync(
                    request,
                    autonomy,
                    directDecision,
                    OperationGatewayOutcome.RolledBack,
                    OpsAutonomyActionOutcome.RolledBack,
                    execution.ExecutionOperationId,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var indeterminateMessage = $"Post-action verification {DescribeVerification(verification.State)}: {verification.Message}; "
            + $"compensation {DescribeCompensation(compensation.State)}: {compensation.Message}; "
            + "the final state is indeterminate; manual intervention required.";
        return await CompleteAutonomyAsync(
                request,
                autonomy,
                directDecision,
                OperationGatewayOutcome.Indeterminate,
                OpsAutonomyActionOutcome.Indeterminate,
                execution.ExecutionOperationId,
                indeterminateMessage,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OperationGatewayResult> CompleteAutonomyAsync(
        OperationGatewayRequest request,
        OpsAutonomyRouteDecision autonomy,
        GuardrailDecision decision,
        OperationGatewayOutcome gatewayOutcome,
        OpsAutonomyActionOutcome autonomyOutcome,
        string? operationId,
        string message,
        CancellationToken cancellationToken)
    {
        await RecordAutonomyOutcomeAsync(
                autonomy,
                autonomyOutcome,
                operationId,
                message,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteAutonomyAuditAsync(
                request,
                GetTerminalAutonomyAuditAction(autonomyOutcome),
                autonomyOutcome == OpsAutonomyActionOutcome.Succeeded
                    ? AuditOutcome.Success
                    : AuditOutcome.Failure,
                message,
                operationId,
                cancellationToken)
            .ConfigureAwait(false);
        await NotifyAutonomyAsync(
                request,
                autonomyOutcome,
                operationId,
                message,
                cancellationToken)
            .ConfigureAwait(false);

        return new OperationGatewayResult
        {
            Outcome = gatewayOutcome,
            Decision = decision,
            ExecutionOperationId = operationId,
            Message = message,
        };
    }

    private async Task RecordPostInvocationCancellationAsync(
        OperationGatewayRequest request,
        OpsAutonomyRouteDecision autonomy,
        string? operationId,
        string message)
    {
        // The caller token is already canceled. Use a short independent budget so the durable
        // reservation, audit row, and manual-intervention alert are not silently lost, while
        // still bounding shutdown latency.
        using var evidenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await CompleteAutonomyAsync(
                    request,
                    autonomy,
                    autonomy.Decision
                        ?? throw new InvalidOperationException("Autonomy route decision did not include a direct-execute guardrail decision."),
                    OperationGatewayOutcome.Canceled,
                    OpsAutonomyActionOutcome.Canceled,
                    operationId,
                    message,
                    evidenceTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: never replace the caller's OperationCanceledException
            // with a secondary evidence persistence failure. The source-generated log is the
            // last-resort breadcrumb.
            Log.AutonomyCancellationEvidenceFailed(
                _logger,
                request.AutonomyContext?.FindingId ?? request.Kind.ToString(),
                ex);
        }
    }

    private static string DescribeVerification(AutonomousVerificationState state)
        => state == AutonomousVerificationState.Failed ? "failed" : "was indeterminate";

    private static string DescribeCompensation(AutonomousCompensationState state)
        => state == AutonomousCompensationState.Failed ? "failed" : "was indeterminate";

    private static string GetTerminalAutonomyAuditAction(OpsAutonomyActionOutcome outcome)
        => outcome switch
        {
            OpsAutonomyActionOutcome.Succeeded => "operation.auto_applied",
            OpsAutonomyActionOutcome.Failed => "operation.auto_failed",
            OpsAutonomyActionOutcome.RolledBack => "operation.auto_rolled_back",
            OpsAutonomyActionOutcome.Indeterminate => "operation.auto_indeterminate",
            OpsAutonomyActionOutcome.Canceled => "operation.auto_canceled",
            _ => "operation.auto_indeterminate",
        };

    private async Task RecordAutonomyOutcomeAsync(
        OpsAutonomyRouteDecision autonomy,
        OpsAutonomyActionOutcome outcome,
        string? operationId,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var evaluator = scope.ServiceProvider.GetService<IOpsAutonomyEvaluator>();
        if (evaluator is not null)
        {
            await evaluator.RecordAutoActionOutcomeAsync(
                    autonomy,
                    outcome,
                    operationId,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordAutonomyProposalRaisedAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AutonomyContext is null)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var evaluator = scope.ServiceProvider.GetService<IOpsAutonomyEvaluator>();
        if (evaluator is not null)
        {
            await evaluator.RecordProposalRaisedAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecordAutonomyProposalResolutionAsync(
        OperationProposal proposal,
        OpsAutonomyProposalResolution resolution,
        CancellationToken cancellationToken)
    {
        if (proposal.AutonomyMetadata is null)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var evaluator = scope.ServiceProvider.GetService<IOpsAutonomyEvaluator>();
        if (evaluator is not null)
        {
            await evaluator.RecordProposalResolutionAsync(proposal, resolution, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task ReconcileAutonomyProposalResolutionAsync(
        OperationProposal proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.ResolvedAt is null || string.IsNullOrWhiteSpace(proposal.ResolvedBy))
        {
            return Task.CompletedTask;
        }

        var resolution = proposal.Status == OperationProposalStatus.Rejected
            ? OpsAutonomyProposalResolution.Rejected
            : OpsAutonomyProposalResolution.Approved;
        return RecordAutonomyProposalResolutionAsync(proposal, resolution, cancellationToken);
    }

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
                Details = BuildProposalAuditDetails(proposal),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAutonomyAuditAsync(
        OperationGatewayRequest request,
        string action,
        AuditOutcome outcome,
        string? message,
        string? operationId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        var actor = request.RequestedBy ?? request.RequestedByAgent ?? AuditEvent.AnonymousActor;
        var actorType = request.RequestedBy is null && request.RequestedByAgent is not null
            ? AuditActorType.System
            : request.RequestedBy is null
                ? AuditActorType.Anonymous
            : AuditActorType.UserId;
        await auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                EventType = AuditEventType.AdminAction,
                Actor = string.IsNullOrWhiteSpace(actor) ? AuditEvent.AnonymousActor : actor,
                ActorType = actorType,
                ResourceType = "operation_autonomy",
                ResourceId = request.AutonomyContext?.FindingId ?? request.IdempotencyKey ?? request.Kind.ToString(),
                Action = action,
                Outcome = outcome,
                CorrelationId = request.CorrelationId
                    ?? operationId
                    ?? request.AutonomyContext?.FindingId
                    ?? Guid.NewGuid().ToString("N"),
                Details = BuildAutonomyAuditDetails(request, message, operationId),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string BuildAutonomyAuditDetails(
        OperationGatewayRequest request,
        string? message,
        string? operationId)
        => "{\"kind\":\"" + JsonEscape(request.Kind.ToString())
            + "\",\"rule\":\"" + JsonEscape(request.AutonomyContext?.Rule)
            + "\",\"findingId\":\"" + JsonEscape(request.AutonomyContext?.FindingId)
            + "\",\"actionDiscriminator\":\"" + JsonEscape(request.ActionDiscriminator ?? request.AutonomyContext?.ActionDiscriminator)
            + "\",\"operationId\":\"" + JsonEscape(operationId)
            + "\",\"evidenceRefs\":" + BuildJsonArray(request.AutonomyContext?.EvidenceRefs)
            + ",\"message\":\"" + JsonEscape(message)
            + "\"}";

    private static string BuildProposalAuditDetails(OperationProposal proposal)
        => "{\"kind\":\"" + JsonEscape(proposal.Kind.ToString())
            + "\",\"status\":\"" + JsonEscape(proposal.Status.ToString())
            + "\",\"rule\":\"" + JsonEscape(proposal.AutonomyMetadata?.Rule)
            + "\",\"findingId\":\"" + JsonEscape(proposal.AutonomyMetadata?.FindingId)
            + "\",\"actionDiscriminator\":\"" + JsonEscape(proposal.AutonomyMetadata?.ActionDiscriminator)
            + "\"}";

    private static OperationProposalAutonomyMetadata? NormalizeAutonomyContext(
        OperationGatewayAutonomyContext? context,
        string? actionDiscriminator)
    {
        if (context is null ||
            !IsBoundedIdentifier(context.FindingId, 256) ||
            !IsBoundedIdentifier(context.Rule, 128))
        {
            return null;
        }

        return new OperationProposalAutonomyMetadata
        {
            FindingId = context.FindingId,
            Rule = context.Rule,
            ActionDiscriminator = IsBoundedIdentifier(actionDiscriminator ?? context.ActionDiscriminator, 128)
                ? actionDiscriminator ?? context.ActionDiscriminator
                : null,
            ActionMarkedAutoSafe = context.ActionMarkedAutoSafe,
            BlastRadius = Math.Max(1, context.BlastRadius),
            EvidenceRefs = context.EvidenceRefs
                .Where(static value => IsBoundedIdentifier(value, 256))
                .Take(16)
                .ToArray(),
        };
    }

    private static bool IsBoundedIdentifier(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(static ch => ch is >= ' ' and <= '~');

    private async Task NotifyAutonomyAsync(
        OperationGatewayRequest request,
        OpsAutonomyActionOutcome outcome,
        string? operationId,
        string? message,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var notifier = scope.ServiceProvider.GetService<OpsNotificationService>();
            if (notifier is null)
            {
                return;
            }

            var context = request.AutonomyContext;
            var findingId = context?.FindingId ?? request.IdempotencyKey ?? request.Kind.ToString();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = request.Kind.ToString(),
                ["outcome"] = outcome.ToString(),
            };
            AddAttribute(attributes, "rule", context?.Rule);
            AddAttribute(attributes, "findingId", context?.FindingId);
            AddAttribute(attributes, "operationId", operationId);
            AddAttribute(attributes, "message", message);
            if (context?.EvidenceRefs is { Count: > 0 } evidenceRefs)
            {
                attributes["evidenceRefs"] = string.Join(",", evidenceRefs);
            }

            var (severity, title) = outcome switch
            {
                OpsAutonomyActionOutcome.Succeeded => (AlertSeverity.Info, "Autonomous remediation converged"),
                OpsAutonomyActionOutcome.RolledBack => (AlertSeverity.Warning, "Autonomous remediation rolled back"),
                OpsAutonomyActionOutcome.Indeterminate => (AlertSeverity.Warning, "Autonomous remediation requires manual intervention"),
                OpsAutonomyActionOutcome.Canceled => (AlertSeverity.Warning, "Autonomous remediation canceled after invocation"),
                _ => (AlertSeverity.Warning, "Autonomous remediation failed"),
            };

            await notifier.NotifyAsync(
                    new OpsNotification
                    {
                        Source = "ops-autonomy",
                        Severity = severity,
                        Title = title,
                        Body = message ?? $"Autonomous remediation outcome: {outcome}.",
                        DedupeIdentifier = $"{findingId}:{outcome}",
                        Attributes = attributes,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AutonomyNotificationFailed(_logger, request.AutonomyContext?.FindingId ?? request.Kind.ToString(), ex);
        }
    }

    private static void AddAttribute(Dictionary<string, string> attributes, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            attributes[key] = value;
        }
    }

    private static string BuildJsonArray(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return "[]";
        }

        return "[\"" + string.Join("\",\"", values.Select(JsonEscape)) + "\"]";
    }

    private static string JsonEscape(string? value)
        => (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

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

        [LoggerMessage(9204, LogLevel.Warning, "Failed to enqueue ops-autonomy notification for finding {FindingId}")]
        public static partial void AutonomyNotificationFailed(ILogger logger, string findingId, Exception exception);

        [LoggerMessage(9205, LogLevel.Error, "Failed to persist canceled ops-autonomy evidence for finding {FindingId}")]
        public static partial void AutonomyCancellationEvidenceFailed(ILogger logger, string findingId, Exception exception);
    }
}
