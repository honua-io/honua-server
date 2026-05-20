// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Metadata v2 lookup helpers scoped to the GeoServices ImageServer handler family.
/// These collapse the "layerId integer → publication → resource" walk that every handler
/// performs into a single call and project the small subset of v1 layer fields the
/// handlers still consume (display name, description, mosaic merge strategy, time info)
/// directly from the V2 graph without any v1 adapter or shim.
/// </summary>
internal static class ImageServerV2Lookups
{
    /// <summary>
    /// Resolved view of an ImageServer publication / resource pair, exposing the small
    /// surface the handlers need without v1 types.
    /// </summary>
    internal readonly record struct ResolvedImageLayer(
        MetadataV2Publication Publication,
        MetadataV2Resource? Resource,
        string DisplayName,
        string? Description);

    /// <summary>
    /// Finds the publication and resource for a numeric Esri layer id by scanning all
    /// publications with a matching <see cref="MetadataV2Publication.LayerIndex"/>.
    /// Returns <c>null</c> when no matching publication is registered.
    /// </summary>
    /// <remarks>
    /// ImageServer routes carry only a layer id (the publication's <c>LayerIndex</c>) and
    /// no service name. To keep the cutover minimal we accept any publication that
    /// matches the integer layer id; gating on Esri-image-specific service or
    /// publication types is left to the route-level validators that already enforce
    /// the ImageServer protocol.
    /// </remarks>
    public static ResolvedImageLayer? FindByLayerIndex(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        foreach (var pub in snapshot.Graph.Publications)
        {
            if (pub.LayerIndex == layerId)
            {
                var resource = snapshot.ResolveResource(pub);
                return Project(pub, resource);
            }
        }

        return null;
    }

    private static ResolvedImageLayer Project(MetadataV2Publication publication, MetadataV2Resource? resource)
    {
        var displayName = publication.TitleOverride
            ?? publication.Metadata.Title
            ?? (string.IsNullOrEmpty(publication.Metadata.Name) ? null : publication.Metadata.Name)
            ?? resource?.Metadata.Title
            ?? (string.IsNullOrEmpty(resource?.Metadata.Name) ? null : resource?.Metadata.Name)
            ?? string.Empty;

        var description = resource?.Metadata.Description ?? publication.Metadata.Description;

        return new ResolvedImageLayer(publication, resource, displayName, description);
    }

    /// <summary>
    /// Returns the temporal field hints declared by the V2 resource's
    /// <c>temporal</c> document, if present. The shape mirrors the legacy
    /// <c>LayerTimeInfo</c> projection so handlers can keep emitting the
    /// ArcGIS-conformant <c>timeInfo</c> block while reading directly from V2.
    /// </summary>
    public static (string? StartTimeField, string? EndTimeField, string? TrackIdField) ReadTimeFieldHints(
        MetadataV2Resource? resource)
    {
        if (resource?.Temporal is not { ValueKind: JsonValueKind.Object } temporal)
        {
            return (null, null, null);
        }

        return (
            ReadStringProperty(temporal, "startTimeField"),
            ReadStringProperty(temporal, "endTimeField"),
            ReadStringProperty(temporal, "trackIdField"));
    }

    /// <summary>
    /// Resolves the mosaic merge strategy directly from the V2 resource and request
    /// override. The request override wins; otherwise the resource's
    /// <c>extensions["rasterMosaic"].mergeStrategy</c> value is used if present.
    /// </summary>
    public static RasterMergeStrategy ResolveMergeStrategy(MetadataV2Resource? resource, string? mosaicRule)
    {
        if (TryParseMergeStrategy(mosaicRule, out var requested))
        {
            return requested;
        }

        if (resource is not null
            && resource.Extensions.TryGetValue("rasterMosaic", out var rasterMosaic)
            && rasterMosaic.ValueKind == JsonValueKind.Object)
        {
            var mergeStrategy = ReadStringProperty(rasterMosaic, "mergeStrategy");
            if (TryParseMergeStrategy(mergeStrategy, out var fromResource))
            {
                return fromResource;
            }
        }

        return RasterMergeStrategy.Newest;
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }
        return null;
    }

    private static bool TryParseMergeStrategy(string? value, out RasterMergeStrategy strategy)
    {
        strategy = RasterMergeStrategy.Newest;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.TryGetProperty("mergeStrategy", out var mergeStrategyProperty))
                {
                    candidate = mergeStrategyProperty.GetString() ?? string.Empty;
                }
                else if (document.RootElement.TryGetProperty("operation", out var operationProperty))
                {
                    candidate = operationProperty.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        var token = candidate.Trim().ToLowerInvariant();
        strategy = token switch
        {
            "newest" or "latest" or "last" or "mt_last" => RasterMergeStrategy.Newest,
            "oldest" or "first" or "mt_first" => RasterMergeStrategy.Oldest,
            "average" or "avg" or "mean" or "mt_mean" => RasterMergeStrategy.Average,
            "max" or "maximum" or "mt_max" => RasterMergeStrategy.Max,
            "min" or "minimum" or "mt_min" => RasterMergeStrategy.Min,
            _ => RasterMergeStrategy.Newest,
        };

        return token is
            "newest" or "latest" or "last" or "mt_last" or
            "oldest" or "first" or "mt_first" or
            "average" or "avg" or "mean" or "mt_mean" or
            "max" or "maximum" or "mt_max" or
            "min" or "minimum" or "mt_min";
    }
}
