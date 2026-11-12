// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Forms;

namespace Honua.Server.Features.Assets;

// Profile commands use the shared resource data-editor check for the published target layer.
// Services are addressed by name or ID in any case; records and command receipts always store the service ID.
internal static class AssetServiceAccess
{
    public static async Task<AssetServiceGrant> ResolveServiceAsync(HttpContext context, string? serviceId)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return AssetServiceGrant.Deny(AssetCommandHttp.Rejected(context, "serviceId", "serviceId is required."));
        }

        var graph = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graph.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        if (snapshot.FindServiceByNameOrId(serviceId) is not { } service)
        {
            return AssetServiceGrant.Deny(
                ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The service was not found."));
        }

        return new AssetServiceGrant(null, service, snapshot);
    }

    public static async Task<AssetServiceGrant> RequireLayerEditorAsync(HttpContext context, string? serviceId, int? layerId)
    {
        var grant = await ResolveServiceAsync(context, serviceId).ConfigureAwait(false);
        return await RequireLayerEditorAsync(context, grant, layerId).ConfigureAwait(false);
    }

    public static async Task<AssetServiceGrant> RequireLayerEditorAsync(HttpContext context, AssetServiceGrant grant, int? layerId)
    {
        if (grant.Denied is not null)
        {
            return grant;
        }

        if (layerId is null)
        {
            return AssetServiceGrant.Deny(AssetCommandHttp.Rejected(context, "layerId", "layerId is required."));
        }

        var target = FormTargetMetadataResolver.Resolve(grant.Snapshot!,
            new FormTargetDefinition { ServiceId = grant.ServiceId, LayerId = layerId.Value });
        if (target.Resource is null)
        {
            return AssetServiceGrant.Deny(AssetCommandHttp.Rejected(context, "layerId", "The published layer was not found."));
        }

        var denied = await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context, target.Resource, target.Service, context.RequestAborted).ConfigureAwait(false);
        return denied is null ? grant : AssetServiceGrant.Deny(denied);
    }

    public static bool Matches(MetadataV2Service service, string? serviceReference)
        => string.Equals(serviceReference, service.Metadata.Id, StringComparison.Ordinal) ||
           string.Equals(serviceReference, service.Metadata.Name, StringComparison.OrdinalIgnoreCase);
}

internal sealed record AssetServiceGrant(IResult? Denied, MetadataV2Service? Service, MetadataV2GraphSnapshot? Snapshot)
{
    public string ServiceId => Service!.Metadata.Id;

    public static AssetServiceGrant Deny(IResult denied) => new(denied, null, null);
}
