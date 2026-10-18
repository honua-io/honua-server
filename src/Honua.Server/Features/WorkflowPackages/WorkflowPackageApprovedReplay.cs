// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.Options;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>Server-only capability minted from a canonical sealed, executing human-approved proposal.</summary>
internal sealed class WorkflowPackageApprovedReplay
{
    private WorkflowPackageApprovedReplay(WorkflowPackageApprovalPayload payload, ClaimsPrincipal principal,
        WorkflowPackageAuthorization authorization, IServiceProvider services)
    {
        Payload = payload;
        Principal = principal;
        Authorization = authorization;
        _services = services;
    }

    private readonly IServiceProvider _services;
    public WorkflowPackageApprovalPayload Payload { get; }
    public ClaimsPrincipal Principal { get; }
    public WorkflowPackageAuthorization Authorization { get; }

    public bool Allows(WorkflowPackageAccess? access, WorkflowPackageGovernanceIntent intent)
        => access is not null && access.WorkspaceAgent && access.ActorId == Payload.ActorId &&
           access.TenantId == Payload.TenantId && access.DelegatedOwnerActorId == Payload.OwnerActorId &&
           JsonSerializer.Serialize(intent, WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageGovernanceIntent) ==
           JsonSerializer.Serialize(Payload.Intent, WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageGovernanceIntent);

    private static async Task RevalidateMembershipAsync(IServiceProvider services, JobSecurityContext snapshot, CancellationToken cancellationToken)
    {
        var membership = await JobSecurityContextCapture.RevalidateRoleMembershipAsync(snapshot,
            services.GetService<IPrincipalMembershipSource>(), services.GetService<IOptions<RbacOptions>>()?.Value ?? new(),
            cancellationToken).ConfigureAwait(false);
        if (membership.HasRemovedRoles || membership.Status is JobSecurityContextMembershipStatus.Changed or
            JobSecurityContextMembershipStatus.Inactive or JobSecurityContextMembershipStatus.ManagedUnresolved) Deny();
    }

    internal async Task RevalidateKeyAndMembershipAsync(CancellationToken cancellationToken)
    {
        var record = await _services.GetRequiredService<IAdminApiKeyStore>().GetAsync(Payload.KeyId, cancellationToken).ConfigureAwait(false);
        var now = _services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (record is null || record.RevokedAt is not null || record.ExpiresAt <= now ||
            !record.Permissions.Order(StringComparer.Ordinal).SequenceEqual(Payload.Permissions.Order(StringComparer.Ordinal))) Deny();
        await RevalidateMembershipAsync(_services, Payload.AuthorSecurityContext, cancellationToken).ConfigureAwait(false);
        // Reuse the full current turn authorizer after the serialization wait, including
        // frozen resources/tasks and native local-process snapshot checks. The private
        // accessor carries the revalidated actual key, never the approving human.
        var access = await Authorization.ResolveAsync(Principal, cancellationToken, Payload.WorkspaceId, Payload.TurnId, true).ConfigureAwait(false);
        var binding = access.TurnBinding;
        if (binding is null || binding.OwnerActorId != Payload.OwnerActorId || binding.TargetId != Payload.TargetId ||
            binding.ReleaseId != Payload.ReleaseId || binding.TenantId != Payload.TenantId) Deny();
        var workspaces = _services.GetRequiredService<IAgentWorkspaceStore>();
        var document = await workspaces.GetAsync(Payload.WorkspaceId).ConfigureAwait(false);
        if (document is null || document.OwnerId != Payload.OwnerActorId ||
            !document.Turns.ContainsKey(Payload.TurnId) ||
            !AgentWorkspaceState.CanRead(document, Payload.ActorId, Payload.TenantId, await workspaces.GetTargetIdAsync().ConfigureAwait(false)) ||
            document.TargetId != Payload.TargetId || _services.GetRequiredService<IConfiguration>()["AgentWorkspace:ReleaseId"] != Payload.ReleaseId) Deny();
    }

    public WorkflowPackageService CreateService(IServiceProvider services)
        => services.GetRequiredService<WorkflowPackageService>().WithAuthorSecurity(new WorkflowAuthorSecurityContextCapturer(
            services.GetService<IOptions<RbacOptions>>(), services.GetRequiredService<ITenantContext>(),
            services.GetService<IPrincipalMembershipSource>(), Authorization, this));

    public static async Task<WorkflowPackageApprovedReplay> VerifyAsync(IServiceProvider services,
        OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken)
    {
        if (request.OperationId != WorkflowPackageGovernanceOperation.OperationId ||
            string.IsNullOrWhiteSpace(context.ApprovedProposalId) || string.IsNullOrWhiteSpace(context.ApprovedPlanHash)) Deny();
        var proposal = await services.GetRequiredService<IOperationProposalStore>()
            .GetAsync(context.ApprovedProposalId!, cancellationToken).ConfigureAwait(false);
        if (proposal is null || proposal.Status != OperationProposalStatus.Executing ||
            proposal.OperationId != request.OperationId || proposal.RequestedBy != context.PrincipalId ||
            proposal.Audit.OperationInstanceId != context.OperationInstanceId ||
            proposal.SealedPlanHash != context.ApprovedPlanHash ||
            proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan) ||
            proposal.Plan.ExecutionPayload is null ||
            !request.Parameters.TryGetValue("sealedPayload", out var supplied) || supplied != proposal.Plan.ExecutionPayload) Deny();
        // Load evidence from storage; never elevate a caller's purported approval DTO.
        var payload = JsonSerializer.Deserialize(proposal!.Plan.ExecutionPayload!,
            WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageApprovalPayload)
            ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        if (payload.ActorId != proposal.RequestedBy || payload.TenantId != context.TenantId ||
            payload.TenantId != services.GetRequiredService<ITenantContext>().TenantId ||
            payload.SchemaName != context.SchemaName || payload.SchemaName != services.GetService<Honua.Core.Features.Infrastructure.Abstractions.ISchemaContext>()?.CurrentSchema) Deny();
        var record = await services.GetRequiredService<IAdminApiKeyStore>().GetAsync(payload.KeyId, cancellationToken).ConfigureAwait(false);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (record is null || record.RevokedAt is not null || record.ExpiresAt <= now ||
            !record.Permissions.Order(StringComparer.Ordinal).SequenceEqual(payload.Permissions.Order(StringComparer.Ordinal))) Deny();
        // This shared factory is the same grant-to-role/canonical-actor mapping as live authentication.
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(record!.Id, record.Name, record.Permissions);
        if (CanonicalSecurityActor.Resolve(principal)?.ActorId != payload.ActorId ||
            !principal.HasClaim("permission", "agent:workspace")) Deny();
        var snapshot = payload.AuthorSecurityContext;
        if (snapshot.OwnerActorId != payload.ActorId || snapshot.TenantId != payload.TenantId ||
            !snapshot.Claims.Where(claim => claim.Type == "permission").Select(claim => claim.Value)
                .Order(StringComparer.Ordinal).SequenceEqual(payload.Permissions.Order(StringComparer.Ordinal))) Deny();
        await RevalidateMembershipAsync(services, snapshot, cancellationToken).ConfigureAwait(false);
        // Carry original claims/RLS inputs into actuation, never newly expanded memberships.
        var restored = JobSecurityContextCapture.Restore(snapshot);
        var pinnedIdentity = new ClaimsIdentity(restored.Claims, AuthenticationExtensions.ApiKeyScheme,
            ClaimTypes.Name, restored.Identity is ClaimsIdentity restoredIdentity ? restoredIdentity.RoleClaimType : ClaimTypes.Role);
        CanonicalSecurityActor.StampAuthorityClaims(pinnedIdentity);
        var pinnedPrincipal = new ClaimsPrincipal(pinnedIdentity);
        if (CanonicalSecurityActor.Resolve(pinnedPrincipal)?.ActorId != payload.ActorId) Deny();
        var privateContext = new DefaultHttpContext { User = principal, RequestServices = services };
        privateContext.Request.Method = HttpMethods.Post;
        privateContext.Request.Headers["X-Honua-Workspace"] = payload.WorkspaceId;
        privateContext.Request.Headers["X-Honua-Turn"] = payload.TurnId;
        var accessor = new ReplayHttpContextAccessor { HttpContext = privateContext };
        // Keep ordinary authorizer ambient-principal equality intact by using an independent
        // accessor instance. Do not replace the current approver's ambient HTTP principal.
        var turns = new AgentWorkspaceTurnAuthorizer(services.GetRequiredService<IAgentWorkspaceStore>(), accessor,
            services.GetRequiredService<IConfiguration>());
        var binding = await turns.AuthorizeAsync(principal, payload.WorkspaceId, payload.TurnId, cancellationToken).ConfigureAwait(false);
        if (binding is null || binding.TenantId != payload.TenantId || binding.TargetId != payload.TargetId ||
            binding.ReleaseId != payload.ReleaseId || binding.OwnerActorId != payload.OwnerActorId) Deny();
        var authorization = new WorkflowPackageAuthorization(accessor, services.GetRequiredService<ITenantContext>(), turns);
        var access = await authorization.ResolveAsync(principal, cancellationToken, payload.WorkspaceId, payload.TurnId, true).ConfigureAwait(false);
        await VerifyResourceAsync(services, payload, access, cancellationToken).ConfigureAwait(false);
        return new(payload, pinnedPrincipal, authorization, services);
    }

    public static async Task VerifyResourceAsync(IServiceProvider services, WorkflowPackageApprovalPayload payload,
        WorkflowPackageAccess access, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IWorkflowPackageStore>();
        var package = await store.GetPackageAsync(payload.Intent.PackageId, cancellationToken).ConfigureAwait(false);
        var version = await store.GetVersionAsync(payload.Intent.PackageId, payload.Intent.Version, cancellationToken).ConfigureAwait(false);
        if (package is null || version is null || !access.CanAccess(package.Ownership) || !access.CanAccess(version.Ownership) ||
            version.PackageHash != payload.Intent.PackageHash) Deny();
        var publication = await store.GetPublicationAsync(payload.Intent.PublicationId, cancellationToken).ConfigureAwait(false);
        if (payload.Intent.Action == "publish")
        {
            if (publication is not null || payload.PublicationFingerprint is not null) Deny();
        }
        else if (publication is null || publication.PackageId != payload.Intent.PackageId ||
                 publication.PackageVersion != payload.Intent.Version || publication.PackageHash != payload.Intent.PackageHash ||
                 Fingerprint(publication) != payload.PublicationFingerprint) Deny();
    }

    public static string Fingerprint(WorkflowPublication publication) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(publication, WorkflowPackageGovernanceJsonContext.Default.WorkflowPublication)));

    // The framework HttpContextAccessor uses static AsyncLocal storage even across instances.
    // This private fixed accessor must never write that ambient storage.
    private sealed class ReplayHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
}
