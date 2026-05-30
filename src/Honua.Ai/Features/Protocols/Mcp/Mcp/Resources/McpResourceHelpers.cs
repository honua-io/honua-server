// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Server.Features.Protocols.Mcp.Models;

namespace Honua.Server.Features.Protocols.Mcp.Resources;

/// <summary>
/// Shared helpers for MCP resource handlers. Centralizes JSON serialization so
/// all resources emit <c>application/json</c> content blocks with identical
/// formatting and AOT-safe source-generated serializers.
/// </summary>
internal static class McpResourceHelpers
{
    public const string JsonMimeType = "application/json";

    /// <summary>
    /// Builds an <see cref="McpResourcesReadResult"/> containing a single
    /// content block whose <c>text</c> is <paramref name="value"/> serialized
    /// as JSON through <paramref name="typeInfo"/>.
    /// </summary>
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
}
