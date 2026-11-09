// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing.Execution;

internal static class FeatureCollectionArtifact
{
    public const string DataUriPrefix = "data:application/geo+json;base64,";

    // The base64 length is checked before decoding so an over-limit payload is rejected without allocating the decoded buffer.
    public static bool TryParseDataUri(
        string? value,
        out FeatureCollection features,
        out string error,
        long maxDecodedBytes = long.MaxValue)
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

        // Guard against over-limit inputs before allocating the full decoded buffer.
        // Base64 encodes 3 bytes as 4 characters, so payload.Length * 3 / 4 is an
        // upper bound on the decoded byte count (padding may shorten it slightly).
        if (maxDecodedBytes < long.MaxValue && (long)payload.Length > maxDecodedBytes / 3L * 4L + 4L)
        {
            error = $"data URI payload exceeds the configured size limit ({maxDecodedBytes} bytes decoded)";
            return false;
        }

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

        if (maxDecodedBytes < long.MaxValue && bytes.Length > maxDecodedBytes)
        {
            error = $"data URI payload decoded size {bytes.Length} bytes exceeds the configured size limit ({maxDecodedBytes} bytes)";
            return false;
        }

        return TryParseJson(Encoding.UTF8.GetString(bytes), out features, out error);
    }

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
            var reader = GeoJsonArtifactCodec.CreateReader();
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

    public static byte[] WriteFeatureCollection(
        IReadOnlyList<IFeature> features,
        string processId,
        IEnumerable<(string Name, object Value)>? extraMembers = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(processId);

        var collection = new FeatureCollection();
        var crs = new FeatureArtifactSpatialReferenceAccumulator();
        foreach (var feature in features)
        {
            collection.Add(feature);
            crs.Add(feature.Geometry);
        }

        var writer = GeoJsonArtifactCodec.CreateWriter();
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
            crs.Value.Write(jsonWriter);
            jsonWriter.WriteString("processId", processId);
            jsonWriter.WriteNumber("featureCount", collection.Count);

            if (extraMembers is not null)
            {
                foreach (var (name, value) in extraMembers)
                {
                    if (string.Equals(name, FeatureArtifactSpatialReference.MemberName, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "Extra members cannot replace the producer coordinate frame.", nameof(extraMembers));
                    }

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
