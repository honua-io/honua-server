// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Rejects an oversized inline raster while its base64 value is still represented by
/// the bounded JSON token, before System.Text.Json allocates the decoded byte array.
/// </summary>
public sealed class BoundedInlineRasterPayloadJsonConverter : JsonConverter<byte[]>
{
    private const int MaximumEncodedBytes =
        ((RasterSourceContract.MaximumInlinePayloadBytes + 2) / 3) * 4;

    public BoundedInlineRasterPayloadJsonConverter()
    {
    }

    public override byte[] Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Inline raster payload must be a base64 string.");
        }

        var encodedLength = reader.HasValueSequence
            ? reader.ValueSequence.Length
            : reader.ValueSpan.Length;
        if (encodedLength > MaximumEncodedBytes)
        {
            throw new JsonException(
                $"Inline raster payload exceeds the {RasterSourceContract.MaximumInlinePayloadBytes}-byte contract ceiling.");
        }

        byte[] payload;
        try
        {
            payload = reader.GetBytesFromBase64();
        }
        catch (FormatException exception)
        {
            throw new JsonException("Inline raster payload is not valid base64.", exception);
        }

        if (payload.Length > RasterSourceContract.MaximumInlinePayloadBytes)
        {
            throw new JsonException(
                $"Inline raster payload exceeds the {RasterSourceContract.MaximumInlinePayloadBytes}-byte contract ceiling.");
        }

        return payload;
    }

    public override void Write(
        Utf8JsonWriter writer,
        byte[] value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteBase64StringValue(value);
    }
}
