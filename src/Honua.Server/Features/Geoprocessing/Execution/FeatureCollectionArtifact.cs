// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Shared in/out contract for the GeoETL transform, source, and sink executors.
/// Every node reads a NetTopologySuite <see cref="FeatureCollection"/> from a
/// <c>data:application/geo+json;base64,&lt;...&gt;</c> data URI and publishes one
/// back through the same envelope, so attributes flow through the workflow DAG
/// (the whole point of ETL). Mirrors <see cref="GeometryFeatureWriter"/>'s single
/// shared encoder so the dispatcher and OGC API Processes surfaces see a uniform
/// document, while reusing the managed NetTopologySuite GeoJSON reader/writer
/// (no native dependency).
/// </summary>
internal static class FeatureCollectionArtifact
{
    /// <summary>
    /// Canonical data URI prefix for a base64-encoded GeoJSON FeatureCollection.
    /// Matches the prefix the geometry executors use for their Feature payloads.
    /// </summary>
    public const string DataUriPrefix = "data:application/geo+json;base64,";

    /// <summary>
    /// Decodes a <see cref="FeatureCollection"/> from a base64 GeoJSON data URI.
    /// Returns <c>false</c> with a classified <paramref name="error"/> for any
    /// malformed input so executors can surface a clean job failure rather than
    /// throwing.
    /// </summary>
    public static bool TryParseDataUri(
        string? value,
        out FeatureCollection features,
        out string error)
    {
        features = new FeatureCollection();
        error = "";

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "value is missing or empty";
            return false;
        }

        if (!value.StartsWith(DataUriPrefix, StringComparison.Ordinal))
        {
            error = $"value must be a '{DataUriPrefix}' data URI";
            return false;
        }

        var payload = value[DataUriPrefix.Length..];
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            error = "data URI payload is not valid base64";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "data URI payload decoded to zero bytes";
            return false;
        }

        return TryParseJson(Encoding.UTF8.GetString(bytes), out features, out error);
    }

    /// <summary>
    /// Parses a raw GeoJSON FeatureCollection document into a
    /// <see cref="FeatureCollection"/>. Used for inline source inputs that supply
    /// the document directly rather than as a data URI.
    /// </summary>
    public static bool TryParseJson(
        string? json,
        out FeatureCollection features,
        out string error)
    {
        features = new FeatureCollection();
        error = "";

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "GeoJSON document is missing or empty";
            return false;
        }

        try
        {
            var reader = new GeoJsonReader();
            var parsed = reader.Read<FeatureCollection>(json);
            if (parsed is null)
            {
                error = "GeoJSON document did not parse to a FeatureCollection";
                return false;
            }

            features = parsed;
            return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException
            or Newtonsoft.Json.JsonException
            or ArgumentException
            or FormatException)
        {
            error = $"GeoJSON document could not be parsed ({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>
    /// Encodes a feature set as a GeoJSON FeatureCollection document. The
    /// <paramref name="processId"/> and optional <paramref name="extraMembers"/>
    /// are written as foreign members alongside <c>type</c> and <c>features</c>
    /// so downstream consumers can trace provenance, mirroring the
    /// <c>geometry.dissolve</c> envelope.
    /// </summary>
    public static byte[] WriteFeatureCollection(
        IReadOnlyList<IFeature> features,
        string processId,
        IEnumerable<(string Name, object Value)>? extraMembers = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(processId);

        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var writer = new GeoJsonWriter();
        var serialized = writer.Write(collection);

        // GeoJsonWriter emits a complete FeatureCollection object. Re-emit it with
        // the provenance foreign members injected, preserving the features array
        // verbatim via raw-text copy so geometry/attribute fidelity is unchanged.
        using var source = System.Text.Json.JsonDocument.Parse(serialized);
        using var buffer = new MemoryStream();
        using (var jsonWriter = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("type", "FeatureCollection");
            jsonWriter.WriteString("processId", processId);
            jsonWriter.WriteNumber("featureCount", collection.Count);

            if (extraMembers is not null)
            {
                foreach (var (name, value) in extraMembers)
                {
                    WriteJsonMember(jsonWriter, name, value);
                }
            }

            jsonWriter.WritePropertyName("features");
            if (source.RootElement.TryGetProperty("features", out var featuresElement))
            {
                featuresElement.WriteTo(jsonWriter);
            }
            else
            {
                jsonWriter.WriteStartArray();
                jsonWriter.WriteEndArray();
            }

            jsonWriter.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Wraps a feature-collection payload in the canonical data URI envelope.
    /// </summary>
    public static string BuildDataUri(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var sb = new StringBuilder(payload.Length * 2 + DataUriPrefix.Length);
        sb.Append(DataUriPrefix);
        sb.Append(Convert.ToBase64String(payload));
        return sb.ToString();
    }

    private static void WriteJsonMember(System.Text.Json.Utf8JsonWriter writer, string name, object value)
    {
        switch (value)
        {
            case int i:
                writer.WriteNumber(name, i);
                break;
            case long l:
                writer.WriteNumber(name, l);
                break;
            case double d:
                writer.WriteNumber(name, d);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            default:
                writer.WriteString(name, value?.ToString() ?? string.Empty);
                break;
        }
    }
}
