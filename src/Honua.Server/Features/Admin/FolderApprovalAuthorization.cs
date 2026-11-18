// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Admin.Domain;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal sealed record FolderProposalReuseContext(
    OperationGatewayRequest Request, OperationProposalPlan Plan, TimeProvider Clock);

internal sealed record FolderProposalAdmission(
    OperationGatewayRequest Operation, OperationProposalPlan Plan, HttpContext? Context);

internal static class FolderApprovalAuthorization
{
    internal const string FreshProposalRequired = "Folder requester authorization changed or is unavailable. Submit a fresh proposal.";

    internal const string NewIdempotencyKeyRequired = "A proposal with this Idempotency-Key was sealed for a different request or authorization. Submit with a new Idempotency-Key.";
    private static readonly TimeSpan MaximumEvidenceLifetime = TimeSpan.FromDays(30);

    internal static async Task<OperationProposalPlan> PrepareAsync(
        FolderProposalAdmission admission, CancellationToken cancellationToken)
    {
        var operation = admission.Operation;
        var plan = admission.Plan;
        var context = admission.Context;
        var (actor, request) = ReadRequesterRequest(operation, plan, context);
        var authenticatedContext = context!;
        var snapshot = await ReadAuthorizedSnapshotAsync(authenticatedContext, request, cancellationToken).ConfigureAwait(false);
        var mutation = BuildMutation(snapshot.Graph, request, operation.TenantId);
        var authorization = new FolderRequesterAuthorization
        {
            RequesterId = actor.ActorId,
            SubjectId = actor.SubjectId,
            SubjectIssuer = actor.SubjectIssuer,
            OperationId = operation.OperationId!,
            SnapshotEtag = snapshot.Etag,
            PolicyRevision = string.Empty,
            ServiceIds = mutation.AffectedServices(snapshot.Graph).Select(service => service.Metadata.Id).Order(StringComparer.Ordinal).ToArray(),
            ExpiresAt = await EvidenceExpiryAsync(authenticatedContext).ConfigureAwait(false)
        };
        var revision = (await PolicyRevisionAsync(new(authenticatedContext.RequestServices, authorization, authenticatedContext.User), cancellationToken).ConfigureAwait(false)).Revision;
        var authorizationContext = CreateAuthorizationContext((snapshot.Graph, mutation), admission, request);
        if ((await FolderServiceAuthorization.EvaluateAsync(authorizationContext, cancellationToken).ConfigureAwait(false)).Failure is not null)
            throw new FolderProposalRefusal("The requester does not have access to every affected service.");
        if (revision != (await PolicyRevisionAsync(new(authenticatedContext.RequestServices, authorization, authenticatedContext.User), cancellationToken).ConfigureAwait(false)).Revision)
            throw new FolderProposalRefusal(FreshProposalRequired);
        return plan with { FolderAuthorization = authorization with { PolicyRevision = revision } };
    }

    private static FolderAuthorizationContext CreateAuthorizationContext(
        (MetadataV2Graph Graph, FolderMutation Mutation) mutation, FolderProposalAdmission admission, OperationRequest request)
    {
        var context = admission.Context!;
        return new FolderAuthorizationContext
        {
            Original = mutation.Graph,
            Mutation = mutation.Mutation,
            Services = context.RequestServices,
            OperatorPrincipal = context.User,
            FolderTenant = request.Parameters.TryGetValue("namespace", out var selectedNamespace) && selectedNamespace is not null ? MetadataV2ServicePaths.NormalizeTenant(admission.Operation.TenantId) : null,
            RequesterAccess = new ServiceAccessContext
            {
                Services = context.RequestServices,
                Principal = context.User,
                TenantId = TenantScopeHelpers.ResolveRequestTenantId(context),
                Operation = AuthorizationOperation.Admin
            }
        };
    }

    private static (CanonicalSecurityActorIdentity Actor, OperationRequest Request) ReadRequesterRequest(
        OperationGatewayRequest operation, OperationProposalPlan plan, HttpContext? context)
    {
        if (context?.User.Identity?.IsAuthenticated != true)
            throw new FolderProposalRefusal("Folder proposals require an active authenticated requester; background callers cannot create them.");
        var actor = CanonicalSecurityActor.Resolve(context.User);
        if (actor is null || !actor.IsDurablyRevalidatable || actor.ActorId != operation.RequestedBy ||
            context.User.IsInRole(AdminApiKeyPermission.ApprovedOperationRole) ||
            !MetadataV2ServicePaths.SameTenant(operation.TenantId, context.RequestServices.GetRequiredService<ITenantContext>().TenantId))
            throw new FolderProposalRefusal("Folder proposals require the authenticated requester's durable identity and tenant.");
        OperationApprovalReplayMapping replay;
        try { replay = FolderReplayRequest.ReadSealedRequest(plan, operation.OperationId, operation.TenantId); }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new FolderProposalRefusal("The folder proposal payload does not match the requested operation, tenant, or mode.");
        }
        if (replay.SchemaName != context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema)
            throw new FolderProposalRefusal("The folder proposal schema does not match the authenticated request.");
        return (actor, replay.Request);
    }

    private static async Task<MetadataV2GraphSnapshot> ReadAuthorizedSnapshotAsync(
        HttpContext context, OperationRequest request, CancellationToken cancellationToken)
    {
        var store = context.RequestServices.GetService<IMetadataV2GraphStore>()
            ?? throw new FolderProposalRefusal(FolderMutationFailure.ReadOnlyCatalog.Detail, OperationGatewayOutcome.Failed);
        var snapshot = await store.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!request.Parameters.TryGetValue("If-Match", out var etag) || string.IsNullOrWhiteSpace(etag))
            throw new FolderProposalRefusal(FolderMutationFailure.MissingPrecondition.Detail, OperationGatewayOutcome.Failed);
        if (etag != snapshot.Etag)
            throw new FolderProposalRefusal(FolderMutationFailure.SnapshotChanged.Detail, OperationGatewayOutcome.Failed);
        if (await context.RequestServices.GetRequiredService<OperatorApprovalGate>().EvaluateAuthorizationAsync(context,
            OperatorResourceType.Catalog, OperatorOperation.Publish).ConfigureAwait(false) is not null)
            throw new FolderProposalRefusal("The requester does not have permission to change the folder catalog.");
        return snapshot;
    }

    private static async Task<DateTimeOffset> EvidenceExpiryAsync(HttpContext context)
    {
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var expiry = now.Add(MaximumEvidenceLifetime);
        var authentication = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult ?? await context.AuthenticateAsync().ConfigureAwait(false);
        if (authentication.Properties?.ExpiresUtc is { } ticketExpiry && ticketExpiry < expiry) expiry = ticketExpiry;
        if (long.TryParse(context.User.FindFirst("exp")?.Value, out var unixExpiry))
        {
            var tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(unixExpiry);
            if (tokenExpiry < expiry) expiry = tokenExpiry;
        }
        if (expiry <= now) throw new FolderProposalRefusal("The requester credential has expired. Authenticate and submit a fresh proposal.");
        return expiry;
    }

    internal static async Task<FolderMutationFailure?> ValidateCurrentAsync(OperationProposal proposal, IServiceProvider services, CancellationToken cancellationToken)
        => (await ResolveCurrentRequesterAsync(proposal, services, cancellationToken).ConfigureAwait(false)).Failure;

    internal static async Task<FolderRequesterCheck> ResolveCurrentRequesterAsync(
        OperationProposal proposal, IServiceProvider services, CancellationToken cancellationToken)
    {
        var changed = new FolderRequesterCheck(null, FolderMutationFailure.RequesterAuthorizationChanged);
        var authorization = proposal.Plan.FolderAuthorization;
        if (authorization is null || authorization.OperationId != proposal.OperationId || authorization.RequesterId != proposal.RequestedBy ||
            authorization.ExpiresAt <= services.GetRequiredService<TimeProvider>().GetUtcNow()) return changed;
        try
        {
            if (proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)) return changed;
            var replay = FolderReplayRequest.ReadSealedRequest(proposal.Plan, proposal.OperationId, proposal.TenantId);
            if (replay.SchemaName != services.GetService<ISchemaContext>()?.CurrentSchema) return changed;
            var policy = await PolicyRevisionAsync(new(services, authorization, null), cancellationToken).ConfigureAwait(false);
            if (authorization.PolicyRevision != policy.Revision) return changed;
            var snapshot = await services.GetRequiredService<IMetadataV2GraphStore>()
                .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Etag != authorization.SnapshotEtag ||
                replay.Request.Parameters.GetValueOrDefault("If-Match") != snapshot.Etag)
                return new(null, FolderMutationFailure.ApprovalSnapshotChanged);
            var affected = BuildMutation(snapshot.Graph, replay.Request, proposal.TenantId)
                .AffectedServices(snapshot.Graph).Select(service => service.Metadata.Id).Order(StringComparer.Ordinal);
            return authorization.ServiceIds.SequenceEqual(affected, StringComparer.Ordinal) ? new(policy.Requester, null) : changed;
        }
        catch (Exception exception) when (exception is FolderProposalRefusal or JsonException or ArgumentException or KeyNotFoundException ||
            (exception is InvalidOperationException && exception is not ServiceUnavailableException))
        {
            return changed;
        }
    }

    internal static bool MatchesExisting(OperationProposal existing, FolderProposalReuseContext candidate)
        => existing.RequestedBy == candidate.Request.RequestedBy && existing.Plan.ExecutionPayload == candidate.Plan.ExecutionPayload &&
            existing.Plan.FolderAuthorization is { } prior && candidate.Plan.FolderAuthorization is { } current &&
            prior.RequesterId == current.RequesterId && prior.OperationId == current.OperationId &&
            prior.ExpiresAt > candidate.Clock.GetUtcNow() && prior.SubjectId == current.SubjectId && prior.SubjectIssuer == current.SubjectIssuer &&
            prior.SnapshotEtag == current.SnapshotEtag && prior.PolicyRevision == current.PolicyRevision &&
            prior.ServiceIds.SequenceEqual(current.ServiceIds, StringComparer.Ordinal);

    private static FolderMutation BuildMutation(MetadataV2Graph graph, OperationRequest request, string? tenantId)
    {
        var parameters = request.Parameters;
        var namespaced = parameters.TryGetValue("namespace", out var selection) && selection is not null;
        var tenant = namespaced ? MetadataV2ServicePaths.NormalizeTenant(tenantId) : null;
        if (!LayerPublicationNamespace.IsValid(selection) || (namespaced && tenant is null)) throw new FolderProposalRefusal(FreshProposalRequired);
        return FolderMutationCommand.FromRequest(request).Apply(graph, tenant);
    }

    private static async Task<ClaimsPrincipal> WriteCredentialPolicyAsync(
        Utf8JsonWriter writer, PolicyReadContext context, CancellationToken cancellationToken)
    {
        var services = context.Services;
        var requesterId = context.Authorization.RequesterId;
        var authority = new OperationProposerApiKeyAuthority(services.GetRequiredService<TimeProvider>(), services.GetService<IAdminApiKeyStore>());
        AdminApiKeyRecord key;
        try { key = await authority.GetCurrentRecordAsync(requesterId, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException exception) when (exception is not ServiceUnavailableException)
        {
            throw new FolderProposalRefusal(OperationProposerApiKeyAuthority.Refusal);
        }
        writer.WriteStartArray();
        writer.WriteStringValue(key.Id);
        writer.WriteStringValue(key.ExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteStringValue(key.RotatedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var values in new[] { key.Permissions, key.AllowedReferrers ?? [], key.AllowedIpRanges ?? [] })
        {
            writer.WriteStartArray();
            foreach (var value in values.Order(StringComparer.Ordinal)) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        var identity = new ClaimsIdentity(AuthenticationExtensions.ApiKeyScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, key.Name));
        identity.AddClaim(new Claim("api_key_id", key.Id.ToString("D")));
        identity.AddClaim(new Claim(ClaimTypes.Role, LayerScopedWriteKey.ConfersFullAdmin(key.Permissions)
            ? OperationPolicyBuiltInRules.FullAdministratorRole : AdminApiKeyPermission.ScopedAdminRole));
        foreach (var permission in key.Permissions)
            identity.AddClaim(new Claim(AdminApiKeyPermission.PermissionClaimType, permission));
        return new ClaimsPrincipal(identity);
    }

    private static async Task<ClaimsPrincipal?> WriteMembershipPolicyAsync(
        Utf8JsonWriter writer, PolicyReadContext context, CancellationToken cancellationToken)
    {
        var authorization = context.Authorization;
        var requester = context.Requester;
        var services = context.Services;
        writer.WriteStartArray();
        writer.WriteStringValue(authorization.SubjectId);
        writer.WriteStringValue(authorization.SubjectIssuer);
        var source = services.GetService<IPrincipalMembershipSource>();
        writer.WriteBooleanValue(source is not null);
        var membership = source is not null && authorization.SubjectId is not null
            ? await source.ResolveMembershipAsync(authorization.SubjectId, authorization.SubjectIssuer, cancellationToken).ConfigureAwait(false)
            : null;
        writer.WriteBooleanValue(membership is not null);
        if (membership is not null)
        {
            if (!membership.IsActive) throw new FolderProposalRefusal(FreshProposalRequired);
            await ValidateMembershipAsync(requester, membership, cancellationToken).ConfigureAwait(false);
            foreach (var role in membership.Roles.Order(StringComparer.Ordinal)) writer.WriteStringValue(role);
        }
        writer.WriteEndArray();
        if (authorization.SubjectId is null) return null;
        var identity = new ClaimsIdentity("FolderRequesterMembership");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, authorization.SubjectId));
        if (authorization.SubjectIssuer is not null)
            identity.AddClaim(new Claim("iss", authorization.SubjectIssuer));
        if (membership is not null)
            foreach (var role in membership.Roles.Where(role => !string.IsNullOrWhiteSpace(role))
                .Select(role => role.Trim()).Distinct(StringComparer.Ordinal))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(identity);
    }

    private static async Task ValidateMembershipAsync(
        ClaimsPrincipal? requester, PrincipalMembership membership, CancellationToken cancellationToken)
    {
        if (requester is null) return;
        var current = await ManagedMembershipRevalidation.ApplyAsync(requester,
            new CapturedMembershipSource(membership), cancellationToken).ConfigureAwait(false);
        if (current.Identity?.IsAuthenticated != true ||
            CanonicalSecurityActor.ResolveAuthorityFingerprint(current) != CanonicalSecurityActor.ResolveAuthorityFingerprint(requester))
            throw new FolderProposalRefusal(FreshProposalRequired);
    }

    // A second lookup could authorize a different membership snapshot than the one sealed.
    private sealed class CapturedMembershipSource(PrincipalMembership membership) : IPrincipalMembershipSource
    {
        public Task<PrincipalMembership?> ResolveMembershipAsync(string principalId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<PrincipalMembership?>(membership);
        }
    }

    // Replay uses the authority read for this revision, never a second lookup or the approver's claims.
    private sealed record CurrentPolicy(string Revision, ClaimsPrincipal Requester);

    private sealed record PolicyReadContext(
        IServiceProvider Services, FolderRequesterAuthorization Authorization, ClaimsPrincipal? Requester);

    private static async Task<CurrentPolicy> PolicyRevisionAsync(
        PolicyReadContext context, CancellationToken cancellationToken)
    {
        var services = context.Services;
        var authorization = context.Authorization;
        using var bytes = new MemoryStream();
        ClaimsPrincipal? currentRequester = null;
        using (var writer = new Utf8JsonWriter(bytes))
        {
            var options = services.GetRequiredService<IOptions<RbacOptions>>().Value;
            writer.WriteStartArray();
            writer.WriteStringValue(options.RoleClaimType);
            writer.WriteStringValue(options.DataEditorServicePrefix);
            writer.WriteStringValue(options.WorkspaceScopeClaimType);
            writer.WriteStartArray();
            foreach (var role in options.DataEditorRoles.Order(StringComparer.Ordinal)) writer.WriteStringValue(role);
            writer.WriteEndArray();
            if (OperationProposerApiKeyAuthority.IsApiKeyActor(authorization.RequesterId))
                currentRequester = await WriteCredentialPolicyAsync(writer, context, cancellationToken).ConfigureAwait(false);
            writer.WriteBooleanValue(LicenseGate.HasLiveEntitlement(services, FeatureCatalog.OidcAuthenticationKey));
            writer.WriteBooleanValue(LicenseGate.HasLiveEntitlement(services, FeatureCatalog.OidcClaimsMappingKey));
            var member = await WriteMembershipPolicyAsync(writer, context, cancellationToken).ConfigureAwait(false);
            currentRequester ??= member;
            await WriteRolePolicyAsync(writer, services, cancellationToken).ConfigureAwait(false);
            writer.WriteEndArray();
        }
        return new(Convert.ToHexString(SHA256.HashData(bytes.ToArray())), currentRequester ??
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, authorization.RequesterId)], "FolderRequester")));
    }

    private static async Task WriteRolePolicyAsync(
        Utf8JsonWriter writer, IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetService<IRoleStore>();
        writer.WriteBooleanValue(store is not null);
        if (store is null) return;
        foreach (var role in (await store.ListRolesAsync(cancellationToken).ConfigureAwait(false)).OrderBy(role => role.RoleId))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(role.RoleId);
            writer.WriteStringValue(role.Name);
            foreach (var grant in (await store.GetPermissionsAsync(role.RoleId, cancellationToken).ConfigureAwait(false))
                .OrderBy(grant => grant.Service, StringComparer.Ordinal).ThenBy(grant => grant.Layer, StringComparer.Ordinal).ThenBy(grant => grant.Operation, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(grant.Service);
                writer.WriteStringValue(grant.Layer);
                writer.WriteStringValue(grant.Operation);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
    }

}

internal sealed class FolderProposalRefusal(string message, OperationGatewayOutcome outcome = OperationGatewayOutcome.Blocked) : Exception(message)
{
    internal OperationGatewayOutcome Outcome { get; } = outcome;
}

internal sealed record FolderRequesterCheck(ClaimsPrincipal? Requester, FolderMutationFailure? Failure);
