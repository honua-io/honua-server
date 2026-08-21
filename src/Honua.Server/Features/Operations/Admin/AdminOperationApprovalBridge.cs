// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
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
        if (!CanonicalSecurityActor.IsBoundIdentity(
                context.PrincipalId,
                context.AuthenticationScheme,
                context.SubjectId,
                context.SubjectIssuer,
                context.ApiKeyId,
                context.CredentialKind))
        {
            return new OperationHandle
            {
                OperationId = request.OperationId,
                HandleId = $"op-{Guid.NewGuid():N}"[..32],
                Status = OperationHandleStatus.Denied,
                Reason = "Approval proposals require an immutable subject or API-key identity.",
            };
        }

        if (request.Parameters.Any(parameter =>
                !string.IsNullOrWhiteSpace(parameter.Value)
                && (CredentialFieldClassifier.IsRawCredential(parameter.Key)
                    || CredentialFieldClassifier.ContainsRawCredential(parameter.Value))))
        {
            return new OperationHandle
            {
                OperationId = request.OperationId,
                HandleId = $"op-{Guid.NewGuid():N}"[..32],
                Status = OperationHandleStatus.Denied,
                Reason = "Raw credentials cannot be persisted in an approval proposal. Use an opaque secret reference.",
            };
        }

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
                // Admin endpoint replay never needs a resolved database connection string.
                // Do not place credential-bearing connection material in the 30-day proposal.
                Context = context with { ResolvedConnectionString = null },
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
    IEnumerable<Honua.Core.Features.Operations.Abstractions.IOperationExecutor> executors,
    AdminOpenApiOperationCatalog catalog,
    AdminEndpointOperationInvoker endpointInvoker,
    IServiceProvider services,
    TimeProvider clock)
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
        Honua.Core.Features.Operations.Abstractions.IOperationExecutor executor;
        if (!_executors.TryGetValue(payload.Action, out executor!))
        {
            AdminOpenApiOperationDefinition definition;
            try
            {
                definition = catalog.GetRequired(payload.Action);
            }
            catch (KeyNotFoundException exception)
            {
                throw new InvalidOperationException(
                    $"Approved admin operation '{payload.Action}' is not registered.",
                    exception);
            }

            executor = new AdminOperationExecutor(definition, catalog, endpointInvoker);
        }

        if (payload.Request.Parameters.Any(parameter =>
                !string.IsNullOrWhiteSpace(parameter.Value)
                && (CredentialFieldClassifier.IsRawCredential(parameter.Key)
                    || CredentialFieldClassifier.ContainsRawCredential(parameter.Value))))
        {
            throw new InvalidOperationException("The approved admin operation payload contains a raw credential.");
        }

        var currentContext = await RevalidateProposerAsync(payload.Context, cancellationToken)
            .ConfigureAwait(false);
        var handle = await executor.SubmitAsync(payload.Request, currentContext, cancellationToken)
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

    private async Task<OperationPolicyContext> RevalidateProposerAsync(
        OperationPolicyContext captured,
        CancellationToken cancellationToken)
    {
        if (!CanonicalSecurityActor.IsBoundIdentity(
                captured.PrincipalId,
                captured.AuthenticationScheme,
                captured.SubjectId,
                captured.SubjectIssuer,
                captured.ApiKeyId,
                captured.CredentialKind))
        {
            throw new InvalidOperationException("The approved operation has no valid immutable proposer identity binding.");
        }

        IReadOnlyList<string> roles;
        IReadOnlyList<string> permissions;
        if (Guid.TryParse(captured.ApiKeyId, out var keyId))
        {
            var keyStore = services.GetService<IAdminApiKeyStore>()
                ?? throw new InvalidOperationException("The proposer API-key store is unavailable.");
            var key = await keyStore.GetAsync(keyId, cancellationToken).ConfigureAwait(false);
            if (key is null || key.RevokedAt is not null
                || (key.ExpiresAt.HasValue && key.ExpiresAt.Value <= clock.GetUtcNow()))
            {
                throw new InvalidOperationException("The proposer API key is revoked, expired, or no longer exists.");
            }

            permissions = key.Permissions;
            roles = ResolveApiKeyRoles(permissions);
        }
        else if (!string.IsNullOrWhiteSpace(captured.SubjectId))
        {
            var userStore = services.GetService<IUserStore>()
                ?? throw new InvalidOperationException("The proposer identity store is unavailable.");
            var user = await userStore.GetUserByPrincipalIdAsync(
                    captured.SubjectId,
                    captured.SubjectIssuer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (user is null || !user.IsActive)
            {
                throw new InvalidOperationException("The proposer identity is disabled or no longer exists.");
            }

            if (user.ProviderId.HasValue)
            {
                var providerStore = services.GetService<IOidcProviderStore>()
                    ?? throw new InvalidOperationException("The proposer OIDC provider store is unavailable.");
                var provider = await providerStore.GetProviderAsync(user.ProviderId.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (provider is null || !provider.Enabled)
                {
                    throw new InvalidOperationException("The proposer OIDC provider is disabled or no longer exists.");
                }
            }

            roles = user.Roles;
            permissions = [];
        }
        else
        {
            throw new InvalidOperationException("The approved operation has no revalidatable proposer identity.");
        }

        EnsureNoDowngrade(captured.Roles, roles, "role");
        EnsureNoDowngrade(captured.Permissions, permissions, "permission");

        if (!string.IsNullOrWhiteSpace(captured.TenantId))
        {
            var tenantCatalog = services.GetService<ITenantCatalog>()
                ?? throw new InvalidOperationException("The proposer tenant catalog is unavailable.");
            var tenant = await tenantCatalog.GetAsync(captured.TenantId, cancellationToken).ConfigureAwait(false);
            if (tenant is null || tenant.Status != TenantStatus.Active)
            {
                throw new InvalidOperationException("The proposer tenant is suspended, deleted, or no longer exists.");
            }
        }

        // Current proposer authorization replaces the captured snapshot. The approver's
        // live principal is intentionally unavailable here and can never be inherited.
        return captured with { Roles = roles, Permissions = permissions };
    }

    private static IReadOnlyList<string> ResolveApiKeyRoles(IReadOnlyList<string> permissions)
    {
        if (LayerScopedWriteKey.IsScopedWriteKey(permissions))
        {
            return [LayerScopedWriteKey.Role];
        }

        return LayerScopedWriteKey.ConfersFullAdmin(permissions)
            ? ["admin"]
            : [LayerScopedWriteKey.ScopedKeyRole];
    }

    private static void EnsureNoDowngrade(
        IReadOnlyList<string> captured,
        IReadOnlyList<string> current,
        string grantType)
    {
        var currentSet = current
            .Select(static value => value.Trim())
            .Where(static value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (captured.Any(value => !string.IsNullOrWhiteSpace(value) && !currentSet.Contains(value.Trim())))
        {
            throw new InvalidOperationException($"The proposer's {grantType} grants were downgraded after proposal creation.");
        }
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
