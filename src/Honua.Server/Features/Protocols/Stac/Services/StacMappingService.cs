// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
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
    /// Builds a STAC Collection directly from a Metadata v2 resource/publication.
    /// </summary>
    /// <remarks>
    /// The feature reader still operates on the integer service-local layer index resolved
    /// from the V2 publication, because the feature-store contract has not yet been ported
    /// (see cutover plan step 2). The mapped collection metadata — license, keywords,
    /// declared extensions — is read from the V2 resource via
    /// <see cref="StacResourceExtensions"/>; spatial/temporal extents come from the V2
    /// resource's spatial/temporal extensions plus the feature store probe.
    /// </remarks>
    public static async Task<StacCollection> MapResourceToCollectionAsync(
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int layerIndex,
        IFeatureReader featureReader,
        string baseUrl,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(featureReader);

        var collectionId = layerIndex.ToString(CultureInfo.InvariantCulture);
        var stacBase = $"{baseUrl}/stac";
        var displayName = ResolveDisplayName(publication, resource, layerIndex);

        var links = ImmutableArray.CreateBuilder<Link>();

        links.Add(Link.Create(
            href: $"{stacBase}/collections/{collectionId}",
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: displayName));

        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Root,
            type: MediaTypes.Json,
            title: "STAC Catalog"));

        links.Add(Link.Create(
            href: stacBase,
            rel: StacConstants.StacRelations.Parent,
            type: MediaTypes.Json,
            title: "STAC Catalog"));

        links.Add(Link.Create(
            href: $"{stacBase}/collections/{collectionId}/items",
            rel: StacConstants.StacRelations.Items,
            type: MediaTypes.GeoJson,
            title: "Items"));

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Alternate,
            type: MediaTypes.Json,
            title: "OGC API Features collection"));

        var extent = await BuildStacExtentV2Async(
            resource,
            layerIndex,
            featureReader,
            coordinateTransformService,
            cancellationToken).ConfigureAwait(false);

        return new StacCollection
        {
            Id = collectionId,
            Title = displayName,
            Description = resource.Metadata.Description ?? $"STAC collection for {displayName}",
            License = resource.ResolveLicense(),
            Extent = extent,
            Keywords = resource.ResolveKeywords(),
            Links = links.ToImmutable(),
            StacExtensions = resource.ResolveDeclaredExtensions()
        };
    }

    private static string ResolveDisplayName(
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        int layerIndex)
    {
        return publication.TitleOverride
            ?? publication.Metadata.Title
            ?? (string.IsNullOrEmpty(publication.Metadata.Name) ? null : publication.Metadata.Name)
            ?? resource.Metadata.Title
            ?? (string.IsNullOrEmpty(resource.Metadata.Name) ? null : resource.Metadata.Name)
            ?? layerIndex.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a STAC item directly from a Metadata v2 resource + publication, sourcing
    /// declared STAC extensions, license, keywords, and temporal field hints from the
    /// resource's STAC + Temporal extensions.
    /// </summary>
    public static StacItem MapFeatureToItem(
        Feature feature,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int layerIndex,
        string baseUrl,
        IReadOnlySet<string>? selectedProperties = null,
        int? geometrySrid = null)
    {
        var collectionId = layerIndex.ToString(CultureInfo.InvariantCulture);
        var itemId = ResolveItemId(feature);
        var escapedItemId = Uri.EscapeDataString(itemId);
        var idFieldName = resource.FindPrimaryIdField()?.Name ?? "objectid";
        var ogcItemId = ResolveOgcPublicId(feature, idFieldName);
        var escapedOgcItemId = Uri.EscapeDataString(ogcItemId);
        var stacBase = $"{baseUrl}/stac";
        IReadOnlyDictionary<string, object?> attributes = feature.Attributes ?? ImmutableDictionary<string, object?>.Empty;
        var selectedPropertiesLookup = selectedProperties is null
            ? null
            : new HashSet<string>(selectedProperties, StringComparer.OrdinalIgnoreCase);

        var properties = new Dictionary<string, object?>();
        PopulateTemporalPropertiesV2(attributes, resource, properties);

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
                if (selectedPropertiesLookup is not null && !selectedPropertiesLookup.Contains(kvp.Key))
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

        JsonElement? geometry = null;
        ImmutableArray<double>? bbox = null;
        if (feature.Geometry is { Length: > 0 })
        {
            try
            {
                var parsed = WkbReaderCache.Get().Read(feature.Geometry);
                geometry = ConvertGeometryToGeoJsonElement(parsed);
                bbox = TryBuildBboxFromGeometry(parsed, geometrySrid ?? resource.ReadSrid() ?? 4326);
            }
            catch
            {
                // WKB parsing failure — STAC allows null geometry.
            }
        }

        var title = resource.Metadata.Title ?? resource.Metadata.Name;

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
                title: title),
            Link.Create(
                href: $"{stacBase}/collections/{collectionId}",
                rel: StacConstants.StacRelations.Parent,
                type: MediaTypes.Json,
                title: title),
            Link.Create(
                href: stacBase,
                rel: StacConstants.StacRelations.Root,
                type: MediaTypes.Json,
                title: "STAC Catalog"));

        var assets = new Dictionary<string, StacAsset>
        {
            ["geojson"] = new StacAsset
            {
                Href = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{escapedOgcItemId}",
                Title = "GeoJSON",
                Type = MediaTypes.GeoJson,
                Roles = ImmutableArray.Create("data"),
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
            StacExtensions = resource.ResolveDeclaredExtensions(),
        };
    }

    private static string ResolveOgcPublicId(Feature feature, string idFieldName)
    {
        if (feature.Attributes is not null &&
            feature.Attributes.TryGetValue(idFieldName, out var configured) &&
            configured is not null)
        {
            var asString = Convert.ToString(configured, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(asString))
            {
                return asString;
            }
        }
        return feature.ObjectId?.ToString(CultureInfo.InvariantCulture)
            ?? feature.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static void PopulateTemporalPropertiesV2(
        IReadOnlyDictionary<string, object?> attributes,
        MetadataV2Resource resource,
        Dictionary<string, object?> properties)
    {
        var fields = resource.ReadTemporalFields();
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (!string.IsNullOrWhiteSpace(fields.StartTimeField))
        {
            start = TryReadTemporalValue(attributes, fields.StartTimeField);
        }
        if (!string.IsNullOrWhiteSpace(fields.EndTimeField))
        {
            end = TryReadTemporalValue(attributes, fields.EndTimeField);
        }

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

    /// <summary>
    /// Builds the STAC extent directly from a V2 resource's spatial/temporal extensions.
    /// </summary>
    private static async Task<StacExtent> BuildStacExtentV2Async(
        MetadataV2Resource resource,
        int layerIndex,
        IFeatureReader featureReader,
        ICoordinateTransformService? coordinateTransformService,
        CancellationToken cancellationToken)
    {
        var bbox = ImmutableArray.Create(ImmutableArray.Create(-180.0, -90.0, 180.0, 90.0));

        var resourceBbox = resource.ReadBbox();
        var srid = resource.ReadSrid() ?? 4326;
        if (resourceBbox is { } extent)
        {
            if (await OgcExtentTransformer.TryTransformExtentToCrs84Async(
                    extent.West,
                    extent.South,
                    extent.East,
                    extent.North,
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

        var temporalInterval = ImmutableArray.Create(ImmutableArray.Create<string?>(null, null));
        var temporalExtent = await OgcFeaturesUtilities.BuildTemporalExtentAsync(
            resource, layerIndex, featureReader, cancellationToken).ConfigureAwait(false);
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
