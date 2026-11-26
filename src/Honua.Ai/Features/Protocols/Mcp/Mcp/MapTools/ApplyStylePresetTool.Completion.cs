// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.MapTools;

internal sealed partial class ApplyStylePresetTool
{
    private static async Task<McpToolsCallResult> ResumeCompletedAsync(
        HttpContext httpContext, OperationHandle operation, CancellationToken cancellationToken)
    {
        var details = operation.Result?.Details
            ?? throw new GeoprocessingPreconditionFailedException("The completed style result is unavailable.");
        var completion = ReadCompletion(details);
        var graph = await httpContext.RequestServices.GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var layer = await MapToolLayerResolver.ResolveForReadAsync(
            httpContext, graph, completion.ServiceId, completion.Argument.ServiceLayerIndex,
            AuthorizationOperation.Admin, cancellationToken).ConfigureAwait(false);
        if (layer.Publication.Metadata.Id != RequiredDetail(details, "expectedPublicationId")
            || layer.Resource.Metadata.Id != RequiredDetail(details, "expectedResourceId")
            || graph.ResolveStorageBinding(layer.Publication)?.Metadata.Id != RequiredDetail(details, "expectedStorageBindingId")
            || layer.StorageLayerId != ReadInteger(details, "expectedStorageLayerId"))
        {
            throw new GeoprocessingPreconditionFailedException("The applied style target is no longer available.");
        }
        return CompletedResult(operation, completion);
    }

    private static StyleCompletion ReadCompletion(IReadOnlyDictionary<string, string> details) => new(
        new McpApplyStylePresetArgument { ServiceLayerIndex = ReadInteger(details, "serviceLayerIndex") },
        RequiredDetail(details, "serviceId"),
        new StyleCatalogRecord
        {
            StyleId = RequiredDetail(details, "styleId"),
            Title = details.GetValueOrDefault("styleTitle"),
            StyleVersion = ReadInteger(details, "styleVersion"),
            MapLibreStyleJson = "{}",
        });

    private static string RequiredDetail(IReadOnlyDictionary<string, string> details, string key)
        => details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new GeoprocessingPreconditionFailedException("The completed style result is unavailable.");

    private static int ReadInteger(IReadOnlyDictionary<string, string> details, string key)
        => int.TryParse(RequiredDetail(details, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new GeoprocessingPreconditionFailedException("The completed style result is invalid.");
}
