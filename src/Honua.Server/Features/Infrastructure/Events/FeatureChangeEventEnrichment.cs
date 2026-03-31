// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Services;

namespace Honua.Server.Features.Infrastructure.Events;

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
        if (feature is null)
        {
            return (null, null);
        }

        var envelope = ExtractEnvelope(feature.Value.Geometry);
        var propertiesJson = SerializeAttributes(feature.Value.Attributes);
        return (envelope, propertiesJson);
    }

    private static double[]? ExtractEnvelope(byte[]? wkb)
    {
        if (wkb is null || wkb.Length == 0)
        {
            return null;
        }

        try
        {
            var reader = WkbReaderCache.Get();
            var geometry = reader.Read(wkb);
            if (geometry is null || geometry.IsEmpty)
            {
                return null;
            }

            var env = geometry.EnvelopeInternal;
            return [env.MinX, env.MinY, env.MaxX, env.MaxY];
        }
        catch
        {
            // Invalid WKB — skip enrichment rather than failing the write.
            return null;
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
