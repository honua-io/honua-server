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
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

internal static class FolderApprovalAuthorization
{
    internal const string FreshProposalRequired = "Folder requester authorization changed or is unavailable. Submit a fresh proposal.";

    internal const string NewIdempotencyKeyRequired = "A proposal with this Idempotency-Key was sealed for a different request or authorization. Submit with a new Idempotency-Key.";
    private static readonly TimeSpan MaximumEvidenceLifetime = TimeSpan.FromDays(30);

    internal static async Task<OperationProposalPlan> PrepareAsync(
        OperationGatewayRequest operation, OperationProposalPlan plan, HttpContext? context, CancellationToken cancellationToken)
    {
        if (context?.User.Identity?.IsAuthenticated != true)
            throw new FolderProposalRefusal("Folder proposals require an active authenticated requester; background callers cannot create them.");
        var actor = CanonicalSecurityActor.Resolve(context.User);
        if (actor is null || !actor.IsDurablyRevalidatable || actor.ActorId != operation.RequestedBy ||
            context.User.IsInRole(AdminApiKeyPermission.ApprovedOperationRole) ||
            !MetadataV2ServicePaths.SameTenant(operation.TenantId, context.RequestServices.GetRequiredService<ITenantContext>().TenantId))
            throw new FolderProposalRefusal("Folder proposals require the authenticated requester's durable identity and tenant.");
        AdminApiOperationApprovalPayload payload;
        try { payload = FolderReplayRequest.ReadSealedPayload(plan, operation.OperationId, operation.TenantId); }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new FolderProposalRefusal("The folder proposal payload does not match the requested operation, tenant, or mode.");
        }
        if (payload.SchemaName != context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema)
            throw new FolderProposalRefusal("The folder proposal schema does not match the authenticated request.");
        var request = payload.ToOperationRequest();
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
            ExpiresAt = await EvidenceExpiryAsync(context).ConfigureAwait(false)
        };
        var revision = await PolicyRevisionAsync(context.RequestServices, authorization, context.User, cancellationToken).ConfigureAwait(false);
        var authorizationContext = CreateAuthorizationContext(context, request, cancellationToken);
        if (await FolderEndpoints.AuthorizeAffectedServices(authorizationContext, snapshot.Graph, mutation).ConfigureAwait(false) is not null)
            throw new FolderProposalRefusal("The requester does not have access to every affected service.");
        if (revision != await PolicyRevisionAsync(context.RequestServices, authorization, context.User, cancellationToken).ConfigureAwait(false))
            throw new FolderProposalRefusal(FreshProposalRequired);
        return plan with { FolderAuthorization = authorization with { PolicyRevision = revision } };
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

    internal static async Task<bool> IsCurrentAsync(OperationProposal proposal, IServiceProvider services, CancellationToken cancellationToken)
    {
        var authorization = proposal.Plan.FolderAuthorization;
        if (authorization is null || authorization.OperationId != proposal.OperationId || authorization.RequesterId != proposal.RequestedBy ||
            authorization.ExpiresAt <= services.GetRequiredService<TimeProvider>().GetUtcNow()) return false;
        try
        {
            if (proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)) return false;
            var payload = FolderReplayRequest.ReadSealedPayload(proposal.Plan, proposal.OperationId, proposal.TenantId);
            if (payload.SchemaName != services.GetService<ISchemaContext>()?.CurrentSchema) return false;
            var snapshot = await services.GetRequiredService<IMetadataV2GraphStore>()
                .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Etag != authorization.SnapshotEtag ||
                payload.Parameters.GetValueOrDefault("If-Match") != snapshot.Etag ||
                authorization.PolicyRevision != await PolicyRevisionAsync(services, authorization, null, cancellationToken).ConfigureAwait(false)) return false;
            var affected = BuildMutation(snapshot.Graph, payload.ToOperationRequest(), proposal.TenantId)
                .AffectedServices(snapshot.Graph).Select(service => service.Metadata.Id).Order(StringComparer.Ordinal);
            return authorization.ServiceIds.SequenceEqual(affected, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is FolderProposalRefusal or JsonException or ArgumentException or KeyNotFoundException ||
            (exception is InvalidOperationException && exception is not ServiceUnavailableException))
        {
            return false;
        }
    }

    internal static bool MatchesExisting(OperationProposal existing, string? requesterId, OperationProposalPlan plan)
        => existing.RequestedBy == requesterId && existing.Plan.ExecutionPayload == plan.ExecutionPayload &&
            existing.Plan.FolderAuthorization is { } prior && plan.FolderAuthorization is { } current &&
            prior.RequesterId == current.RequesterId && prior.OperationId == current.OperationId &&
            prior.ExpiresAt > DateTimeOffset.UtcNow && prior.SubjectId == current.SubjectId && prior.SubjectIssuer == current.SubjectIssuer &&
            prior.SnapshotEtag == current.SnapshotEtag && prior.PolicyRevision == current.PolicyRevision &&
            prior.ServiceIds.SequenceEqual(current.ServiceIds, StringComparer.Ordinal);

    private static FolderMutation BuildMutation(MetadataV2Graph graph, OperationRequest request, string? tenantId)
    {
        var parameters = request.Parameters;
        var namespaced = parameters.TryGetValue("namespace", out var selection) && selection is not null;
        var tenant = namespaced ? MetadataV2ServicePaths.NormalizeTenant(tenantId) : null;
        if (!LayerPublicationNamespace.IsValid(selection) || (namespaced && tenant is null)) throw new FolderProposalRefusal(FreshProposalRequired);
        var folder = new FolderRequest { Name = parameters.GetValueOrDefault("name") };
        if (parameters.TryGetValue("title", out var title)) folder.Title = title;
        return request.OperationId switch
        {
            "folder.create" => FolderMutation.Create(graph, folder, tenant),
            "folder.update" => FolderMutation.Update(graph, parameters["id"]!, folder, tenant),
            "folder.delete" => FolderMutation.Delete(graph, parameters["id"]!, tenant),
            "service.move" => FolderMutation.Move(graph, request.ServiceName!,
                parameters.TryGetValue("folderId", out var id) ? new MetadataV2FolderSelection(id) : default, tenant),
            _ => throw new FolderProposalRefusal(FreshProposalRequired)
        };
    }

    private static DefaultHttpContext CreateAuthorizationContext(HttpContext context, OperationRequest request, CancellationToken cancellationToken)
    {
        var authorization = new DefaultHttpContext { RequestServices = context.RequestServices, User = context.User, RequestAborted = cancellationToken };
        FolderReplayRequest.BindRoute(request, authorization.Request);
        return authorization;
    }

    private static async Task WriteCredentialPolicyAsync(Utf8JsonWriter writer, IServiceProvider services,
        string requesterId, CancellationToken cancellationToken)
    {
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
    }

    private static async Task WriteMembershipPolicyAsync(Utf8JsonWriter writer, IServiceProvider services,
        FolderRequesterAuthorization authorization, ClaimsPrincipal? requester, CancellationToken cancellationToken)
    {
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
            if (requester is not null)
            {
                var current = await ManagedMembershipRevalidation.ApplyAsync(requester,
                    new CapturedMembershipSource(membership), cancellationToken).ConfigureAwait(false);
                if (current.Identity?.IsAuthenticated != true ||
                    CanonicalSecurityActor.ResolveAuthorityFingerprint(current) != CanonicalSecurityActor.ResolveAuthorityFingerprint(requester))
                    throw new FolderProposalRefusal(FreshProposalRequired);
            }
            foreach (var role in membership.Roles.Order(StringComparer.Ordinal)) writer.WriteStringValue(role);
        }
        writer.WriteEndArray();
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

    private static async Task<string> PolicyRevisionAsync(IServiceProvider services, FolderRequesterAuthorization authorization,
        ClaimsPrincipal? requester, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
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
                await WriteCredentialPolicyAsync(writer, services, authorization.RequesterId, cancellationToken).ConfigureAwait(false);
            writer.WriteBooleanValue(LicenseGate.HasLiveEntitlement(services, FeatureCatalog.OidcClaimsMappingKey));
            await WriteMembershipPolicyAsync(writer, services, authorization, requester, cancellationToken).ConfigureAwait(false);
            var store = services.GetService<IRoleStore>();
            writer.WriteBooleanValue(store is not null);
            if (store is not null)
            {
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
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}

internal sealed class FolderProposalRefusal(string message, OperationGatewayOutcome outcome = OperationGatewayOutcome.Blocked) : Exception(message)
{
    internal OperationGatewayOutcome Outcome { get; } = outcome;
}
