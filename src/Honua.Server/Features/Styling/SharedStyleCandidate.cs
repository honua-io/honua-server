// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Styling;

internal sealed record SharedStyleCandidate(string MapLibreJson, string DrawingInfoJson);

/// <summary>Uses the existing strict validators and converters without writing a style.</summary>
internal static class SharedStyleCandidatePreparation
{
    public static async Task<(SharedStyleCandidate? Candidate, OgcStyleUpdateResult? Error)> PrepareAsync(
        ResolvedStyleEdit current, string document, bool drawingInfo, HttpContext context, CancellationToken cancellationToken)
    {
        JsonElement input;
        try { using var parsed = JsonDocument.Parse(document); input = parsed.RootElement.Clone(); }
        catch (JsonException) { return (null, new(OgcStyleUpdateStatus.Invalid, "The style document is not valid JSON.")); }
        string canonical;
        if (drawingInfo)
        {
            if (!OgcStyleProjection.TryValidateDrawingInfoShape(input, out var rendererError))
                return (null, new(OgcStyleUpdateStatus.Invalid, rendererError));
            var descriptor = current.PrimaryResource is { } primary && current.Impact.CanonicalLayerId is { } layer
                ? StyleLayerDescriptor.FromResource(primary, layer)
                : StandaloneStyleDescriptor.FromMapLibre(current.Impact.StyleId, current.CanonicalStyleJson);
            if (!descriptor.IsBoundToStorageLayer)
                return (null, new(OgcStyleUpdateStatus.Invalid, "This style is not bound to a Honua layer; edit its canonical MapLibre document."));
            var graph = await context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (!graph.Index.ResourcesByStorageLayerId.TryGetValue(descriptor.Id, out var resource))
                return (null, new(OgcStyleUpdateStatus.Invalid, "The style source cannot be resolved safely."));
            if (!(await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource, null,
                    AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false)).IsAllowed)
                return (null, new(OgcStyleUpdateStatus.Forbidden, "The style source is not accessible."));
            var geometry = resource.ReadGeometryType();
            if (geometry == MetadataV2GeometryType.None || !StandaloneStyleDescriptor.TryInferConsistentGeometryType(
                input, out var rendererGeometry, out var unsupported, out _))
                return (null, new(OgcStyleUpdateStatus.Invalid, "The renderer geometry cannot be converted safely."));
            if (rendererGeometry != MetadataV2GeometryType.None && !OgcStyleProjection.UsesSameGeometryFamily(geometry, rendererGeometry))
                return (null, new(OgcStyleUpdateStatus.Invalid, "The renderer does not match the bound source geometry."));
            var conversion = context.RequestServices.GetRequiredService<IGeoServicesStyleConverter>()
                .Convert(input, descriptor.Id, current.Impact.StyleId, geometry);
            var warnings = conversion.Unsupported.Select(item => $"{item.Code} ({item.SymbolizerType}): {item.Guidance}").ToList();
            if (unsupported) warnings.Add("unsupported-renderer-content: Renderer content cannot be preserved losslessly.");
            if (warnings.Count > 0) return (null, new(OgcStyleUpdateStatus.Invalid,
                "Shared style editing refuses a lossy renderer conversion. Edit the canonical MapLibre document.", warnings));
            canonical = conversion.MapLibreStyleJson;
            using var converted = JsonDocument.Parse(canonical);
            input = converted.RootElement.Clone();
        }
        else canonical = input.GetRawText();
        if (current.PrimaryResource is { } resourceForLayer && current.Impact.CanonicalLayerId is { } layerId)
        {
            if (!MapLibreStyleNormalizer.TryNormalize(input, StyleLayerDescriptor.FromResource(resourceForLayer, layerId), out canonical, out var normalizationError))
                return (null, new(OgcStyleUpdateStatus.Invalid, normalizationError));
        }
        else if (!MapLibreStyleNormalizer.TryValidateStandalone(input, out var validationError))
            return (null, new(OgcStyleUpdateStatus.Invalid, validationError));
        var styleDescriptor = current.PrimaryResource is { } bound && current.Impact.CanonicalLayerId is { } id
            ? StyleLayerDescriptor.FromResource(bound, id)
            : StandaloneStyleDescriptor.FromMapLibre(current.Impact.StyleId, canonical);
        return (new(canonical, MapLibreToGeoServicesConverter.Convert(canonical, styleDescriptor)), null);
    }
}
