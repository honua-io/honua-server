// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Services;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Extracts geometry envelope and attribute snapshot from a feature for CDC event enrichment.
/// Called at publish sites so filter evaluation during broadcast requires no I/O.
/// </summary>
internal static class FeatureChangeEventEnrichment
{
    /// <summary>
    /// Extracts enrichment data from a feature. Returns null envelope and properties for null features (deletes).
    /// </summary>
    public static (double[]? GeometryEnvelope, string? PropertiesJson) FromFeature(Feature? feature)
    {
        var enrichment = FromFeatureSnapshot(feature);
        return (enrichment.GeometryEnvelope, enrichment.PropertiesJson);
    }

    /// <summary>
    /// Extracts all streamable enrichment data from a feature snapshot. The optional
    /// <paramref name="fallbackSrid"/> is applied when the WKB itself carries no SRID
    /// metadata (e.g., gRPC ApplyEdits and WFS Transaction publish features whose
    /// WKB was written by a default <c>WKBWriter</c> with <c>handleSRID:false</c>).
    /// Without this fallback the geodesy invariant would silently drop the GeoJSON
    /// even when the layer CRS is known.
    /// </summary>
    public static FeatureChangeEventEnrichmentResult FromFeatureSnapshot(Feature? feature, int? fallbackSrid = null)
    {
        if (feature is null)
        {
            return default;
        }

        var (envelope, geometryJson, geometrySrid) = ExtractGeometry(feature.Value.Geometry, fallbackSrid);
        var propertiesJson = SerializeAttributes(feature.Value.Attributes);
        return new FeatureChangeEventEnrichmentResult(envelope, propertiesJson, geometryJson, geometrySrid);
    }

    private static (double[]? Envelope, string? GeometryJson, int? Srid) ExtractGeometry(byte[]? wkb, int? fallbackSrid)
    {
        if (wkb is null || wkb.Length == 0)
        {
            return (null, null, null);
        }

        try
        {
            var reader = WkbReaderCache.Get();
            var geometry = reader.Read(wkb);
            if (geometry is null || geometry.IsEmpty)
            {
                return (null, null, null);
            }

            var env = geometry.EnvelopeInternal;
            // Geodesy invariant: never emit geometry coordinates without CRS metadata.
            // Prefer the SRID written into the WKB; otherwise fall back to the layer
            // SRID provided by the caller (gRPC/WFS mutation paths whose default
            // WKBWriter does not embed SRID). The envelope is retained for
            // broadcast-time bbox filter evaluation, which compares against the
            // subscription bbox already projected into the layer's storage CRS.
            var srid = geometry.SRID > 0
                ? geometry.SRID
                : fallbackSrid is > 0 ? fallbackSrid : null;
            var geometryJson = srid.HasValue ? new GeoJsonWriter().Write(geometry) : null;
            return ([env.MinX, env.MinY, env.MaxX, env.MaxY], geometryJson, srid);
        }
        catch
        {
            // Invalid WKB — skip enrichment rather than failing the write.
            return (null, null, null);
        }
    }

    private static string? SerializeAttributes(ImmutableDictionary<string, object?>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(attributes, FeatureChangeEventEnrichmentJsonContext.Default.ImmutableDictionaryStringObject);
        }
        catch
        {
            // Unsupported runtime attribute value — skip enrichment rather than failing the write.
            return null;
        }
    }
}

internal readonly record struct FeatureChangeEventEnrichmentResult(
    double[]? GeometryEnvelope,
    string? PropertiesJson,
    string? GeometryJson,
    int? GeometrySrid);

/// <summary>
/// Source-generated JSON context for attribute snapshot serialization (AOT compatible).
/// Registers all primitive types that <c>object?</c> values resolve to at runtime,
/// following the same pattern as <c>FeatureAttributesJsonContext</c>.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ImmutableDictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(object[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(object))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(long))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(float))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(byte[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Guid))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DateTime))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DateTimeOffset))]
[System.Text.Json.Serialization.JsonSerializable(typeof(DateOnly))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeOnly))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TimeSpan))]
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal sealed partial class FeatureChangeEventEnrichmentJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
