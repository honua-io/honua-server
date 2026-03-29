// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.Stac.Models;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Stac.Services;

/// <summary>
/// Maps Honua catalog entities (layers, services, features) to STAC representations.
/// </summary>
internal sealed class StacMappingService
{
    /// <summary>
    /// Builds a STAC Collection from a layer definition.
    /// </summary>
    public static async Task<StacCollection> MapLayerToCollectionAsync(
        LayerDefinition layer,
        IFeatureReader featureReader,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
        var stacBase = $"{baseUrl}/stac";

        var links = ImmutableArray.CreateBuilder<Link>();

        // Self
        links.Add(Link.Create(
            href: $"{stacBase}/collections/{collectionId}",
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: layer.Name));

        // Root
        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Root,
            type: MediaTypes.Json,
            title: "STAC Catalog"));

        // Parent
        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Parent,
            type: MediaTypes.Json,
            title: "STAC Catalog"));

        // Items
        links.Add(Link.Create(
            href: $"{stacBase}/collections/{collectionId}/items",
            rel: StacConstants.StacRelations.Items,
            type: MediaTypes.GeoJson,
            title: "Items"));

        // Cross-protocol links: OGC API Features
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Alternate,
            type: MediaTypes.Json,
            title: "OGC API Features collection"));

        var extent = BuildStacExtent(layer, featureReader, cancellationToken);

        return new StacCollection
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description ?? $"STAC collection for {layer.Name}",
            License = "proprietary",
            Extent = await extent,
            Links = links.ToImmutable()
        };
    }

    /// <summary>
    /// Maps a Honua feature to a STAC Item.
    /// </summary>
    public static StacItem MapFeatureToItem(
        Feature feature,
        LayerDefinition layer,
        string baseUrl)
    {
        var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
        var itemId = feature.ObjectId?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var stacBase = $"{baseUrl}/stac";

        var properties = new Dictionary<string, object?>();

        // STAC requires a "datetime" property — use the layer's time field if available,
        // otherwise null (valid per spec when start_datetime/end_datetime are present or no temporal info exists).
        var datetimeResolved = false;
        if (layer.Metadata?.TimeInfo is { StartTimeField: { } startField })
        {
            if (feature.Attributes.TryGetValue(startField, out var startVal) && startVal is DateTimeOffset dto)
            {
                properties["datetime"] = dto.ToString("o", CultureInfo.InvariantCulture);
                datetimeResolved = true;
            }
        }

        if (!datetimeResolved)
        {
            properties["datetime"] = null;
        }

        // Copy feature attributes
        foreach (var kvp in feature.Attributes)
        {
            if (!string.Equals(kvp.Key, "objectid", StringComparison.OrdinalIgnoreCase))
            {
                properties[kvp.Key] = kvp.Value;
            }
        }

        // Build geometry as JSON element
        JsonElement? geometry = null;
        if (feature.Geometry is { Length: > 0 })
        {
            geometry = ConvertWkbToGeoJsonElement(feature.Geometry);
        }

        var links = ImmutableArray.Create(
            Link.Create(
                href: $"{stacBase}/collections/{collectionId}/items/{itemId}",
                rel: RelationTypes.Self,
                type: MediaTypes.GeoJson,
                title: $"Item {itemId}"),
            Link.Create(
                href: $"{stacBase}/collections/{collectionId}",
                rel: RelationTypes.Collection,
                type: MediaTypes.Json,
                title: layer.Name),
            Link.Create(
                href: stacBase,
                rel: StacConstants.StacRelations.Root,
                type: MediaTypes.Json,
                title: "STAC Catalog"));

        // Cross-protocol asset links
        var assets = new Dictionary<string, StacAsset>
        {
            ["geojson"] = new StacAsset
            {
                Href = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{itemId}",
                Title = "GeoJSON",
                Type = MediaTypes.GeoJson,
                Roles = ImmutableArray.Create("data")
            }
        };

        return new StacItem
        {
            Id = itemId,
            Geometry = geometry,
            Properties = properties,
            Links = links,
            Assets = assets,
            Collection = collectionId
        };
    }

    /// <summary>
    /// Builds the STAC extent from a layer's spatial and temporal metadata.
    /// </summary>
    private static async Task<StacExtent> BuildStacExtent(
        LayerDefinition layer,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        // Spatial extent
        var bbox = ImmutableArray.Create(ImmutableArray.Create(-180.0, -90.0, 180.0, 90.0));
        if (layer.Extent is { } extent)
        {
            var srid = extent.SpatialReference;
            if (srid == 4326)
            {
                bbox = ImmutableArray.Create(ImmutableArray.Create(
                    extent.MinX, extent.MinY, extent.MaxX, extent.MaxY));
            }
            else if (OgcExtentTransformer.TryTransformToCrs84(extent.MinX, extent.MinY, srid, out var min) &&
                     OgcExtentTransformer.TryTransformToCrs84(extent.MaxX, extent.MaxY, srid, out var max))
            {
                bbox = ImmutableArray.Create(ImmutableArray.Create(min.Lon, min.Lat, max.Lon, max.Lat));
            }
        }

        // Temporal extent
        var temporalInterval = ImmutableArray.Create(ImmutableArray.Create<string?>(null, null));
        var temporalExtent = await OgcFeaturesUtilities.BuildTemporalExtentAsync(layer, featureReader, cancellationToken);
        if (temporalExtent is not null)
        {
            temporalInterval = temporalExtent.Interval;
        }

        return new StacExtent
        {
            Spatial = new StacSpatialExtent { Bbox = bbox },
            Temporal = new StacTemporalExtent { Interval = temporalInterval }
        };
    }

    /// <summary>
    /// Converts WKB geometry bytes to a GeoJSON JsonElement.
    /// Returns null if conversion fails — STAC allows null geometry.
    /// </summary>
    private static JsonElement? ConvertWkbToGeoJsonElement(byte[] wkb)
    {
        try
        {
            var reader = new WKBReader();
            var geom = reader.Read(wkb);
            var writer = new GeoJsonWriter();
            var json = writer.Write(geom);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
