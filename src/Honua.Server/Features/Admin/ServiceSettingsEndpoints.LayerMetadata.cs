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
        var target = ResolveFeatureLayerTarget(snapshot, serviceName, layerId);
        if (target.Error is not null)
            return target.Error;
        var resource = target.Resource!;
        context.Response.Headers.ETag = snapshot.Etag;
        return Results.Json(ApiResponse<LayerMetadataResponse>.CreateSuccess(BuildLayerMetadataResponse(
            layerId, resource.Metadata.Name, resource.Metadata, resource.AccessPolicy, resource.Temporal, null, resource.Editing)),
            ServiceSettingsJsonContext.Default.ApiResponseLayerMetadataResponse);
    }

    private sealed record FeatureLayerTarget(MetadataV2Resource? Resource, IResult? Error);

    private static FeatureLayerTarget ResolveFeatureLayerTarget(MetadataV2GraphSnapshot snapshot, string serviceName, int layerId)
    {
        if (!TryResolveServicesByName(snapshot, serviceName, out var services))
            return new(null, TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found.")));
        var serviceIds = services.Where(service => ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer) &&
                IsFeatureServerService(snapshot.Graph, service))
            .Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        if (serviceIds.Count == 0)
            return new(null, TypedResults.Problem(title: "FeatureServer service not found",
                detail: $"Service '{serviceName}' does not resolve to a FeatureServer service.", statusCode: 409));
        var resourceIds = FindLayerMetadataResourceIds(snapshot, serviceIds, layerId);
        if (resourceIds.Length > 1)
            return new(null, TypedResults.Problem(title: "Ambiguous FeatureServer layer",
                detail: $"Layer {layerId} in service '{serviceName}' resolves to multiple resources.", statusCode: 409));
        return resourceIds.Length == 1 && snapshot.Index.ResourcesById.TryGetValue(resourceIds[0], out var resource)
            ? new(resource, null)
            : new(null, TypedResults.NotFound(ApiResponse<object>.Failure($"Layer {layerId} not found in service '{serviceName}'.")));
    }

    private static string[] FindLayerMetadataResourceIds(MetadataV2GraphSnapshot snapshot,
        HashSet<string> serviceIds, int layerId) => snapshot.Graph.Publications
        .Where(publication => serviceIds.Contains(publication.ServiceId) &&
            publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
            snapshot.IsRoutable(publication) && publication.LayerIndex == layerId)
        .Select(publication => publication.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
}
