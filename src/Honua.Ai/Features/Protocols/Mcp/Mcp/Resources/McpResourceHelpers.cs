// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Resources;

internal static class McpResourceHelpers
{
    public const string JsonMimeType = "application/json";

    public static McpResourcesReadResult SingleJsonContent<T>(
        string uri,
        T value,
        JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        return new McpResourcesReadResult
        {
            Contents =
            [
                new McpResourceContent
                {
                    Uri = uri,
                    MimeType = JsonMimeType,
                    Text = json
                }
            ]
        };
    }

    public static McpResourcesReadResult SingleJsonContent(string uri, JsonElement value) => new()
    {
        Contents =
        [
            new McpResourceContent
            {
                Uri = uri,
                MimeType = JsonMimeType,
                Text = value.GetRawText()
            }
        ]
    };
}
