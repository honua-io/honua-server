// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Geoprocessing.Raster;

public sealed class BoundedInlineRasterOutputPayloadJsonConverter : JsonConverter<byte[]>
{
    private const int MaximumEncodedBytes =
        ((RasterOutputContract.MaximumInlinePayloadBytes + 2) / 3) * 4;
    private const long MaximumEscapedTokenBytes = (long)MaximumEncodedBytes * 6;

    public override byte[] Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Inline raster output payload must be a base64 string.");
        }

        var encodedLength = reader.HasValueSequence
            ? reader.ValueSequence.Length
            : reader.ValueSpan.Length;
        // A legal JSON string can spell one logical base64 character as a six-byte Unicode
        // escape (for example '=' spelled as backslash-u003d). Bound that worst case before
        // asking the reader to unescape/decode, then enforce the authoritative decoded-byte
        // ceiling below.
        if (encodedLength > MaximumEscapedTokenBytes)
        {
            throw new JsonException(
                $"Inline raster output payload exceeds the {RasterOutputContract.MaximumInlinePayloadBytes}-byte contract ceiling.");
        }

        byte[] payload;
        try
        {
            payload = reader.GetBytesFromBase64();
        }
        catch (FormatException exception)
        {
            throw new JsonException("Inline raster output payload is not valid base64.", exception);
        }

        if (payload.Length > RasterOutputContract.MaximumInlinePayloadBytes)
        {
            throw new JsonException(
                $"Inline raster output payload exceeds the {RasterOutputContract.MaximumInlinePayloadBytes}-byte contract ceiling.");
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
