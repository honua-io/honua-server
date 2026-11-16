// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Models;

internal sealed class McpDiscoverResult
{
    [JsonPropertyName("supportedVersions")]
    public IReadOnlyList<string> SupportedVersions { get; set; } = McpModernProtocol.SupportedVersions;

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities Capabilities { get; set; } = new()
    {
        Extensions = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["io.modelcontextprotocol/skills"] = System.Text.Json.JsonSerializer.SerializeToElement(
                new Dictionary<string, System.Text.Json.JsonElement>(), McpJsonContext.Default.DictionaryStringJsonElement)
        }
    };
}
