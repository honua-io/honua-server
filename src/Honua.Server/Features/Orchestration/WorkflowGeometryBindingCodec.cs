// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Geoprocessing.Execution;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Honua.Server.Features.Orchestration;

internal readonly record struct WkbExtractionResult(string? Value, string? Error)
{
    public bool IsSuccess => Error is null && Value is not null;

    public static WkbExtractionResult Success(string value) => new(value, null);

    public static WkbExtractionResult Failure(string error) => new(null, error);
}

internal static class WorkflowGeometryBindingCodec
{
    private const string DataUriPrefix = "data:";
    // Same ceiling as the largest allowed Geoprocessing:Executors:MaxArtifactBytes; the producer enforces its configured cap.
    private const int MaxDecodedPayloadBytes = 1024 * 1024 * 1024;
    private const int MaxBase64PayloadChars = ((MaxDecodedPayloadBytes + 2) / 3) * 4;

    public static bool IsDataUri(string? uri)
        => uri is not null && uri.StartsWith(DataUriPrefix, StringComparison.OrdinalIgnoreCase);

    public static WkbExtractionResult ExtractWkb(string dataUri)
    {
        var (geometries, error) = ExtractGeometriesFromDataUri(dataUri);
        if (error is not null)
        {
            return WkbExtractionResult.Failure(error);
        }

        if (geometries!.Count != 1)
        {
            return WkbExtractionResult.Failure(
                $"GeoJSON payload contains {geometries.Count} geometries; expected a single geometry.");
        }

        return WkbExtractionResult.Success(EncodeStandardWkbBase64(geometries[0]));
    }

    public static WkbExtractionResult ExtractWkbArray(string dataUri)
    {
        var (geometries, error) = ExtractGeometriesFromDataUri(dataUri);
        if (error is not null)
        {
            return WkbExtractionResult.Failure(error);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var geometry in geometries!)
            {
                writer.WriteStringValue(EncodeStandardWkbBase64(geometry));
            }

            writer.WriteEndArray();
        }

        return WkbExtractionResult.Success(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static (List<Geometry>? Geometries, string? Error) ExtractGeometriesFromDataUri(string dataUri)
    {
        var (payloadBytes, decodeError) = DecodeGeoJsonDataUri(dataUri);
        if (decodeError is not null)
        {
            return (null, decodeError);
        }

        try
        {
            using var document = JsonDocument.Parse(payloadBytes!);
            return ExtractGeometries(document.RootElement);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (null, $"GeoJSON artifact could not be parsed as JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException
            or ParseException
            or ArgumentException
            or FormatException
            or InvalidCastException
            or InvalidOperationException
            or IndexOutOfRangeException
            or NotSupportedException
            or OverflowException)
        {
            return (null, $"GeoJSON geometry could not be decoded: {ex.Message}");
        }
    }

    private static (byte[]? Payload, string? Error) DecodeGeoJsonDataUri(string dataUri)
    {
        if (!IsDataUri(dataUri))
        {
            return (null, "artifact URI must be a data: URI.");
        }

        var commaIndex = dataUri.IndexOf(',');
        if (commaIndex <= DataUriPrefix.Length)
        {
            return (null, "artifact data URI is missing a media type or payload separator.");
        }

        var headerError = ValidateDataUriHeader(dataUri.AsSpan(DataUriPrefix.Length, commaIndex - DataUriPrefix.Length));
        if (headerError is not null)
        {
            return (null, headerError);
        }

        return DecodeBase64Payload(dataUri.AsSpan(commaIndex + 1).Trim());
    }

    private static string? ValidateDataUriHeader(ReadOnlySpan<char> header)
    {
        var mediaType = ReadOnlySpan<char>.Empty;
        var isBase64 = false;
        var first = true;
        foreach (var range in header.Split(';'))
        {
            var token = header[range].Trim();
            if (first)
            {
                mediaType = token;
                first = false;
                continue;
            }

            if (token.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                isBase64 = true;
            }
        }

        if (!isBase64)
        {
            return "artifact data URI must be base64-encoded.";
        }

        if (!mediaType.Equals("application/geo+json", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return $"unsupported artifact media type '{mediaType.ToString()}'; expected application/geo+json or application/json.";
        }

        return null;
    }

    private static (byte[]? Payload, string? Error) DecodeBase64Payload(ReadOnlySpan<char> encodedSpan)
    {
        if (encodedSpan.IsEmpty)
        {
            return (null, "artifact data URI payload is empty.");
        }

        if (encodedSpan.Length > MaxBase64PayloadChars)
        {
            return (null, $"artifact payload exceeds maximum supported size of {MaxDecodedPayloadBytes} bytes.");
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(encodedSpan.ToString());
        }
        catch (FormatException)
        {
            return (null, "artifact data URI payload is not valid base64.");
        }

        return payloadBytes.Length == 0 || payloadBytes.Length > MaxDecodedPayloadBytes
            ? (null, "artifact payload is empty or exceeds maximum supported size.")
            : (payloadBytes, null);
    }

    private static (List<Geometry>? Geometries, string? Error) ExtractGeometries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return (null, "GeoJSON payload must be an object with a string 'type' property.");
        }

        var reader = GeoJsonArtifactCodec.CreateReader();
        return typeElement.GetString() switch
        {
            "Feature" => WrapSingle(ReadFeatureGeometry(root, reader)),
            "FeatureCollection" => ExtractFeatureCollection(root, reader),
            "Point" or "MultiPoint" or "LineString" or "MultiLineString"
                or "Polygon" or "MultiPolygon" or "GeometryCollection"
                => WrapSingle(ReadGeometryElement(root, reader)),
            var other => (null, $"unsupported GeoJSON type '{other}'.")
        };
    }

    private static (List<Geometry>? Geometries, string? Error) ExtractFeatureCollection(
        JsonElement root,
        GeoJsonReader reader)
    {
        if (!root.TryGetProperty("features", out var featuresElement)
            || featuresElement.ValueKind != JsonValueKind.Array)
        {
            return (null, "GeoJSON FeatureCollection is missing a 'features' array.");
        }

        var geometries = new List<Geometry>();
        foreach (var feature in featuresElement.EnumerateArray())
        {
            var (geometry, error) = ReadFeatureGeometry(feature, reader);
            if (error is not null)
            {
                return (null, error);
            }

            geometries.Add(geometry!);
        }

        return geometries.Count == 0
            ? (null, "GeoJSON FeatureCollection contains no features.")
            : (geometries, null);
    }

    private static (List<Geometry>? Geometries, string? Error) WrapSingle((Geometry? Geometry, string? Error) item)
        => item.Error is not null ? (null, item.Error) : ([item.Geometry!], null);

    private static (Geometry? Geometry, string? Error) ReadFeatureGeometry(
        JsonElement feature,
        GeoJsonReader reader)
    {
        if (feature.ValueKind != JsonValueKind.Object
            || !feature.TryGetProperty("geometry", out var geometryElement)
            || geometryElement.ValueKind != JsonValueKind.Object)
        {
            return (null, "GeoJSON Feature has a null or missing geometry.");
        }

        return ReadGeometryElement(geometryElement, reader);
    }

    private static (Geometry? Geometry, string? Error) ReadGeometryElement(
        JsonElement geometryElement,
        GeoJsonReader reader)
    {
        var geometry = reader.Read<Geometry>(geometryElement.GetRawText());
        return geometry is null || geometry.IsEmpty
            ? (null, "GeoJSON geometry is empty.")
            : (geometry, null);
    }

    private static string EncodeStandardWkbBase64(Geometry geometry)
    {
        var hasZ = false;
        var hasM = false;
        foreach (var coordinate in geometry.Coordinates)
        {
            if (!double.IsNaN(coordinate.Z))
            {
                hasZ = true;
            }

            if (!double.IsNaN(coordinate.M))
            {
                hasM = true;
            }

            if (hasZ && hasM)
            {
                break;
            }
        }

        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
        return Convert.ToBase64String(writer.Write(geometry));
    }
}
