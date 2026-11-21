// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Infrastructure.Helpers;

namespace Honua.Infrastructure.Caching;

// Serializes a value for hashing into a pooled buffer. The bytes are those SerializeToUtf8Bytes returns,
// without a new array the size of the response (a large-object-heap allocation above 85 KB).
internal static class ETagJsonPayload
{
    private const int DefaultMaxDepth = 64;

    internal static PooledByteBuffer Serialize(object? value, JsonTypeInfo typeInfo)
    {
        var buffer = new PooledByteBuffer();
        try
        {
            using (var writer = new Utf8JsonWriter((IBufferWriter<byte>)buffer, WriterOptions(typeInfo.Options)))
            {
                JsonSerializer.Serialize(writer, value, typeInfo);
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    // The writer options JsonSerializer derives from its own options for SerializeToUtf8Bytes.
    internal static JsonWriterOptions WriterOptions(JsonSerializerOptions options) => new()
    {
        Encoder = options.Encoder,
        Indented = options.WriteIndented,
        IndentCharacter = options.IndentCharacter,
        IndentSize = options.IndentSize,
        NewLine = options.NewLine,
        MaxDepth = options.MaxDepth == 0 ? DefaultMaxDepth : options.MaxDepth,
        SkipValidation = true,
    };
}
