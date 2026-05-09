// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Stac.Models;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Stac.Services;

/// <summary>
/// Maps Honua catalog entities (layers, services, features) to STAC representations.
/// </summary>
internal sealed class StacMappingService
{
    [ThreadStatic]
    private static GeoJsonWriter? _geoJsonWriter;

    private static GeoJsonWriter GetGeoJsonWriter() => _geoJsonWriter ??= new GeoJsonWriter();
    /// <summary>
    /// Builds a STAC Collection from a layer definition.
    /// </summary>
    public static async Task<StacCollection> MapLayerToCollectionAsync(
        LayerDefinition layer,
        IFeatureReader featureReader,
        string baseUrl,
        ICoordinateTransformService? coordinateTransformService,
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

        var extent = BuildStacExtent(layer, featureReader, coordinateTransformService, cancellationToken);

        return new StacCollection
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description ?? $"STAC collection for {layer.Name}",
            License = ResolveLicense(layer),
            Extent = await extent,
            Keywords = ResolveKeywords(layer),
            Links = links.ToImmutable(),
            StacExtensions = ResolveDeclaredExtensions(layer)
        };
    }

    /// <summary>
    /// Maps a Honua feature to a STAC Item.
    /// </summary>
    public static StacItem MapFeatureToItem(
        Feature feature,
        LayerDefinition layer,
        string baseUrl,
        IReadOnlySet<string>? selectedProperties = null,
        int? geometrySrid = null)
    {
        var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
        var itemId = ResolveItemId(feature);
        var escapedItemId = Uri.EscapeDataString(itemId);
        var ogcItemId = OgcFeatureIdentifierResolver.FormatPublicId(feature, layer);
        var escapedOgcItemId = Uri.EscapeDataString(ogcItemId);
        var stacBase = $"{baseUrl}/stac";
        IReadOnlyDictionary<string, object?> attributes = feature.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        var selectedPropertiesLookup = selectedProperties is null
            ? null
            : new HashSet<string>(selectedProperties, StringComparer.OrdinalIgnoreCase);

        var properties = new Dictionary<string, object?>();
        PopulateTemporalProperties(attributes, layer, properties);

        // Copy feature attributes
        foreach (var kvp in attributes)
        {
            if ((!IsItemIdAttribute(kvp.Key) || selectedPropertiesLookup?.Contains(kvp.Key) == true) &&
                !string.Equals(kvp.Key, "objectid", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kvp.Key, "datetime", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kvp.Key, "start_datetime", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kvp.Key, "end_datetime", StringComparison.OrdinalIgnoreCase) &&
                !FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
            {
                if (selectedPropertiesLookup is not null &&
                    !selectedPropertiesLookup.Contains(kvp.Key))
                {
                    continue;
                }

                if (kvp.Value is null && selectedPropertiesLookup is null)
                {
                    continue;
                }

                properties[kvp.Key] = kvp.Value;
            }
        }

        // Build geometry as JSON element
        JsonElement? geometry = null;
        ImmutableArray<double>? bbox = null;
        if (feature.Geometry is { Length: > 0 })
        {
            try
            {
                var parsed = WkbReaderCache.Get().Read(feature.Geometry);
                geometry = ConvertGeometryToGeoJsonElement(parsed);
                bbox = TryBuildBboxFromGeometry(parsed, geometrySrid ?? layer.SpatialReference.Wkid);
            }
            catch
            {
                // WKB parsing failure — STAC allows null geometry.
            }
        }

        var links = ImmutableArray.Create(
            Link.Create(
                href: $"{stacBase}/collections/{collectionId}/items/{escapedItemId}",
                rel: RelationTypes.Self,
                type: MediaTypes.GeoJson,
                title: $"Item {itemId}"),
            Link.Create(
                href: $"{stacBase}/collections/{collectionId}",
                rel: RelationTypes.Collection,
                type: MediaTypes.Json,
                title: layer.Name),
            Link.Create(
                href: $"{stacBase}/collections/{collectionId}",
                rel: StacConstants.StacRelations.Parent,
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
                Href = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{escapedOgcItemId}",
                Title = "GeoJSON",
                Type = MediaTypes.GeoJson,
                Roles = ImmutableArray.Create("data")
            }
        };

        return new StacItem
        {
            Id = itemId,
            Geometry = geometry,
            Bbox = bbox,
            Properties = properties,
            Links = links,
            Assets = assets,
            Collection = collectionId,
            StacExtensions = ResolveDeclaredExtensions(layer)
        };
    }

    private static string ResolveItemId(Feature feature)
    {
        foreach (var key in new[] { "stac_id", "item_id", "id" })
        {
            if (feature.Attributes is null || !feature.Attributes.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var resolved = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        return feature.ObjectId?.ToString(CultureInfo.InvariantCulture) ?? "0";
    }

    private static bool IsItemIdAttribute(string attributeName)
        => attributeName.Equals("id", StringComparison.OrdinalIgnoreCase) ||
           attributeName.Equals("stac_id", StringComparison.OrdinalIgnoreCase) ||
           attributeName.Equals("item_id", StringComparison.OrdinalIgnoreCase);

    private static void PopulateTemporalProperties(
        IReadOnlyDictionary<string, object?> attributes,
        LayerDefinition layer,
        Dictionary<string, object?> properties)
    {
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        try
        {
            var temporalFields = TemporalExtentHelpers.ResolveTemporalFieldsOrThrow(layer);
            start = TryReadTemporalValue(attributes, temporalFields.StartField.Name);
            if (temporalFields.EndField is not null)
            {
                end = TryReadTemporalValue(attributes, temporalFields.EndField.Name);
            }
        }
        catch (ArgumentException)
        {
            // Layers without temporal metadata still serialize a null datetime, which keeps the
            // shape stable even when no attribute can be promoted into STAC temporal fields.
        }

        // When the layer has no temporal metadata but the feature carries STAC interval
        // fields (start_datetime + end_datetime), reconstruct the interval before falling
        // back to a single-value probe that would discard the end.
        if (start is null && end is null)
        {
            var intervalStart = TryReadTemporalValue(attributes, "start_datetime");
            if (intervalStart is not null)
            {
                start = intervalStart;
                end = TryReadTemporalValue(attributes, "end_datetime");
            }
        }

        start ??= TryReadFallbackTemporalValue(attributes);

        if (start is not null && (end is null || end == start))
        {
            properties["datetime"] = FormatTemporalValue(start.Value);
            return;
        }

        if (start is not null || end is not null)
        {
            properties["datetime"] = null;
            properties["start_datetime"] = start is null ? null : FormatTemporalValue(start.Value);
            properties["end_datetime"] = end is null ? null : FormatTemporalValue(end.Value);
            return;
        }

        properties["datetime"] = null;
    }

    private static string ResolveLicense(LayerDefinition layer)
    {
        var declaredLicense = layer.Metadata?.Stac?.License;
        return string.IsNullOrWhiteSpace(declaredLicense)
            ? "proprietary"
            : declaredLicense.Trim();
    }

    private static ImmutableArray<string>? ResolveKeywords(LayerDefinition layer)
    {
        var keywords = layer.Metadata?.Stac?.Keywords;
        if (keywords is null || keywords.Length == 0)
        {
            return null;
        }

        var normalized = keywords
            .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(static keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return normalized.Length == 0 ? null : normalized;
    }

    private static ImmutableArray<string>? ResolveDeclaredExtensions(LayerDefinition layer)
    {
        var extensions = layer.Metadata?.Stac?.Extensions;
        if (extensions is null || extensions.Length == 0)
        {
            return null;
        }

        var normalized = extensions
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .Select(static extension => extension.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// Builds the STAC extent from a layer's spatial and temporal metadata.
    /// </summary>
    private static async Task<StacExtent> BuildStacExtent(
        LayerDefinition layer,
        IFeatureReader featureReader,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        // Spatial extent
        var bbox = ImmutableArray.Create(ImmutableArray.Create(-180.0, -90.0, 180.0, 90.0));
        if (layer.Extent is { } extent)
        {
            var srid = extent.SpatialReference;
            if (await OgcExtentTransformer.TryTransformExtentToCrs84Async(
                    extent.MinX,
                    extent.MinY,
                    extent.MaxX,
                    extent.MaxY,
                    srid,
                    coordinateTransformService,
                    cancellationToken).ConfigureAwait(false) is { } transformedExtent)
            {
                bbox = ImmutableArray.Create(ImmutableArray.Create(
                    transformedExtent.MinLon,
                    transformedExtent.MinLat,
                    transformedExtent.MaxLon,
                    transformedExtent.MaxLat));
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
    /// Converts a parsed geometry to a GeoJSON JsonElement.
    /// Returns null if serialization fails — STAC allows null geometry.
    /// </summary>
    private static JsonElement? ConvertGeometryToGeoJsonElement(NetTopologySuite.Geometries.Geometry geom)
    {
        try
        {
            var json = GetGeoJsonWriter().Write(geom);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadTemporalValue(
        IReadOnlyDictionary<string, object?> attributes,
        string fieldName)
    {
        if (!attributes.TryGetValue(fieldName, out var value))
        {
            return null;
        }

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => dateTime.Kind switch
            {
                DateTimeKind.Utc => new DateTimeOffset(dateTime, TimeSpan.Zero),
                DateTimeKind.Local => new DateTimeOffset(dateTime.ToUniversalTime(), TimeSpan.Zero),
                _ => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero)
            },
            DateOnly dateOnly => new DateTimeOffset(
                dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                TimeSpan.Zero),
            string text when DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedDateTimeOffset) => parsedDateTimeOffset,
            string text when DateOnly.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDateOnly) => new DateTimeOffset(
                    parsedDateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    TimeSpan.Zero),
            JsonElement element when element.ValueKind == JsonValueKind.String => TryReadTemporalValue(
                new Dictionary<string, object?> { [fieldName] = element.GetString() },
                fieldName),
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var milliseconds) =>
                DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            long milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            double milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(
                Convert.ToInt64(milliseconds, CultureInfo.InvariantCulture)),
            _ => null
        };
    }

    private static string FormatTemporalValue(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset? TryReadFallbackTemporalValue(
        IReadOnlyDictionary<string, object?> attributes)
    {
        ReadOnlySpan<string> candidates =
        [
            "datetime",
            "created_at",
            "updated_at",
            "start_datetime",
            "timestamp",
            "event_date",
            "date"
        ];

        foreach (var candidate in candidates)
        {
            var value = TryReadTemporalValue(attributes, candidate);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static ImmutableArray<double>? TryBuildBboxFromGeometry(NetTopologySuite.Geometries.Geometry geom, int srid)
    {
        try
        {
            var envelope = geom.EnvelopeInternal;

            if (srid == 4326)
            {
                return ImmutableArray.Create(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
            }

            if (OgcExtentTransformer.TryTransformToCrs84(envelope.MinX, envelope.MinY, srid, out var min) &&
                OgcExtentTransformer.TryTransformToCrs84(envelope.MaxX, envelope.MaxY, srid, out var max))
            {
                return ImmutableArray.Create(min.Lon, min.Lat, max.Lon, max.Lat);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
