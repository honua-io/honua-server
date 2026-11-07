// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Admin.Domain;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
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

    internal static async Task<OperationProposalPlan> PrepareAsync(
        string operationId, string? requesterId, string? tenantId, OperationProposalPlan plan,
        HttpContext? context, CancellationToken cancellationToken)
    {
        if (context is null || context.User.Identity?.IsAuthenticated != true ||
            context.User.IsInRole(AdminApiKeyPermission.ApprovedOperationRole) ||
            CanonicalSecurityActor.Resolve(context.User)?.ActorId is not { } actor || actor != requesterId ||
            !MetadataV2ServicePaths.SameTenant(tenantId, context.RequestServices.GetRequiredService<ITenantContext>().TenantId))
            throw new FolderProposalRefusal(FreshProposalRequired);
        var payload = ReadPayload(operationId, tenantId, plan);
        if (payload.SchemaName != context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema)
            throw new FolderProposalRefusal(FreshProposalRequired);
        var request = payload.ToOperationRequest();
        var snapshot = await context.RequestServices.GetRequiredService<IMetadataV2GraphStore>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (request.Parameters.GetValueOrDefault("If-Match") != snapshot.Etag)
            throw new FolderProposalRefusal("The metadata snapshot has changed or If-Match is missing. Refresh and submit a fresh proposal.", OperationGatewayOutcome.Failed);
        if (await context.RequestServices.GetRequiredService<OperatorApprovalGate>().EvaluateAuthorizationAsync(context,
            OperatorResourceType.Catalog, OperatorOperation.Publish).ConfigureAwait(false) is not null)
            throw new FolderProposalRefusal("The requester does not have permission to change the folder catalog.");
        var mutation = BuildMutation(snapshot.Graph, request, tenantId);
        var policyRevision = await PolicyRevisionAsync(context.RequestServices, requesterId!, cancellationToken).ConfigureAwait(false);
        var authorizationContext = CreateAuthorizationContext(context, request, cancellationToken);
        if (await FolderEndpoints.AuthorizeAffectedServices(authorizationContext, snapshot.Graph, mutation).ConfigureAwait(false) is not null)
            throw new FolderProposalRefusal("The requester does not have access to every affected service.");
        if (policyRevision != await PolicyRevisionAsync(context.RequestServices, requesterId!, cancellationToken).ConfigureAwait(false))
            throw new FolderProposalRefusal(FreshProposalRequired);
        var ticket = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = ticket.Properties?.ExpiresUtc;
        if (long.TryParse(context.User.FindFirst("exp")?.Value, out var unixExpiry))
        {
            var tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(unixExpiry);
            if (expiry is null || tokenExpiry < expiry) expiry = tokenExpiry;
        }
        if (expiry <= context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow())
            throw new FolderProposalRefusal(FreshProposalRequired);
        return plan with
        {
            FolderAuthorization = new FolderRequesterAuthorization
            {
                RequesterId = actor, OperationId = operationId, SnapshotEtag = snapshot.Etag,
                PolicyRevision = policyRevision,
                ServiceIds = mutation.AffectedServices(snapshot.Graph).Select(service => service.Metadata.Id).Order(StringComparer.Ordinal).ToArray(),
                ExpiresAt = expiry
            }
        };
    }

    internal static async Task<bool> IsCurrentAsync(OperationProposal proposal, HttpContext context, CancellationToken cancellationToken)
    {
        var authorization = proposal.Plan.FolderAuthorization;
        if (authorization is null || authorization.OperationId != proposal.OperationId || authorization.RequesterId != proposal.RequestedBy ||
            authorization.ExpiresAt <= context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()) return false;
        try
        {
            var payload = ReadPayload(proposal.OperationId!, proposal.TenantId, proposal.Plan);
            var snapshot = await context.RequestServices.GetRequiredService<IMetadataV2GraphStore>()
                .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.Etag != authorization.SnapshotEtag ||
                payload.Parameters.GetValueOrDefault("If-Match") != snapshot.Etag ||
                authorization.PolicyRevision != await PolicyRevisionAsync(context.RequestServices, authorization.RequesterId, cancellationToken).ConfigureAwait(false)) return false;
            var affected = BuildMutation(snapshot.Graph, payload.ToOperationRequest(), proposal.TenantId)
                .AffectedServices(snapshot.Graph).Select(service => service.Metadata.Id).Order(StringComparer.Ordinal);
            return authorization.ServiceIds.SequenceEqual(affected, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or FolderProposalRefusal)
        {
            return false;
        }
    }

    internal static bool MatchesExisting(OperationProposal existing, string? requesterId, OperationProposalPlan plan)
        => existing.RequestedBy == requesterId && existing.Plan.ExecutionPayload == plan.ExecutionPayload &&
            existing.Plan.FolderAuthorization is { } prior && plan.FolderAuthorization is { } current &&
            prior.RequesterId == current.RequesterId && prior.OperationId == current.OperationId &&
            prior.SnapshotEtag == current.SnapshotEtag && prior.PolicyRevision == current.PolicyRevision &&
            prior.ServiceIds.SequenceEqual(current.ServiceIds, StringComparer.Ordinal);

    private static AdminApiOperationApprovalPayload ReadPayload(string operationId, string? tenantId, OperationProposalPlan plan)
    {
        var payload = plan.ExecutionPayload is null ? null : JsonSerializer.Deserialize(plan.ExecutionPayload,
            AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload);
        if (payload is null || payload.OperationId != operationId || payload.DryRun ||
            !MetadataV2ServicePaths.SameTenant(payload.TenantId, tenantId)) throw new FolderProposalRefusal(FreshProposalRequired);
        return payload;
    }

    private static FolderMutation BuildMutation(MetadataV2Graph graph, OperationRequest request, string? tenantId)
    {
        var parameters = request.Parameters;
        var tenant = parameters.ContainsKey("namespace") ? MetadataV2ServicePaths.NormalizeTenant(tenantId) : null;
        if (!LayerPublicationNamespace.IsValid(parameters.GetValueOrDefault("namespace")) ||
            (parameters.ContainsKey("namespace") && tenant is null)) throw new FolderProposalRefusal(FreshProposalRequired);
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
        var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == request.OperationId);
        authorization.Request.Method = definition.Method.Method;
        authorization.Request.Path = "/api/v1/admin" + AdminApiOperationExecutor.BindPath(definition, request);
        if (request.Parameters.TryGetValue("namespace", out var selection))
            authorization.Request.QueryString = QueryString.Create("namespace", selection);
        return authorization;
    }

    private static async Task WriteCredentialPolicyAsync(Utf8JsonWriter writer, IServiceProvider services,
        string requesterId, CancellationToken cancellationToken)
    {
        if (!OperationProposerApiKeyAuthority.TryGetKeyId(requesterId, out var id))
            throw new FolderProposalRefusal(FreshProposalRequired);
        var key = await services.GetRequiredService<IAdminApiKeyStore>().GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (key is null || key.Id != id || key.RevokedAt is not null ||
            key.ExpiresAt <= services.GetRequiredService<TimeProvider>().GetUtcNow()) throw new FolderProposalRefusal(FreshProposalRequired);
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

    private static async Task<string> PolicyRevisionAsync(IServiceProvider services, string requesterId, CancellationToken cancellationToken)
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
            if (OperationProposerApiKeyAuthority.IsApiKeyActor(requesterId))
                await WriteCredentialPolicyAsync(writer, services, requesterId, cancellationToken).ConfigureAwait(false);
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
