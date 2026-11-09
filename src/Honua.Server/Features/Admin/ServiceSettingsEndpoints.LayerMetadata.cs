// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static partial class ServiceSettingsEndpoints
{
    private static async Task<IResult> HandleGetLayerMetadata(string serviceName, int layerId, HttpContext context)
    {
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var store = context.RequestServices.GetService<IMetadataV2GraphStore>();
        var snapshot = await GetSnapshotForServiceAsync(provider, store, serviceName, context.RequestAborted)
            .ConfigureAwait(false);
        if (!TryResolveServicesByName(snapshot, serviceName, out var services))
            return TypedResults.NotFound(ApiResponse<object>.Failure("Service not found."));
        var featureServiceIds = services.Where(service =>
                ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer) && IsFeatureServerService(snapshot.Graph, service))
            .Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        if (featureServiceIds.Count == 0)
            return TypedResults.Problem(statusCode: 409, detail: "The service has no FeatureServer publication.");
        var resourceIds = FindLayerMetadataResourceIds(snapshot, featureServiceIds, layerId);
        if (resourceIds.Length > 1)
            return TypedResults.Problem(statusCode: 409, detail: "The service layer resolves to multiple resources.");
        if (resourceIds.Length == 0)
            return TypedResults.NotFound(ApiResponse<object>.Failure("Layer not found in service."));
        var resource = snapshot.Index.ResourcesById[resourceIds[0]];
        context.Response.Headers.ETag = snapshot.Etag;
        return Results.Json(ApiResponse<LayerMetadataResponse>.CreateSuccess(BuildLayerMetadataResponse(
            layerId, resource.Metadata.Name, resource.Metadata, resource.AccessPolicy, resource.Temporal, null, resource.Editing)),
            ServiceSettingsJsonContext.Default.ApiResponseLayerMetadataResponse);
    }

    private static string[] FindLayerMetadataResourceIds(MetadataV2GraphSnapshot snapshot,
        IReadOnlySet<string> serviceIds, int layerId) => snapshot.Graph.Publications
        .Where(publication => serviceIds.Contains(publication.ServiceId) &&
            publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
            snapshot.IsRoutable(publication) && publication.LayerIndex == layerId)
        .Select(publication => publication.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
}
