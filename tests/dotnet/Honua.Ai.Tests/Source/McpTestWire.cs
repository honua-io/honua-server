// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public enum McpTestTransport
{
    Legacy,
    Modern
}

internal static class McpTestWire
{
    private const string ModernProtocolVersion = "2026-07-28";
    internal const string ModernMetadata = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"" + ModernProtocolVersion
        + "\",\"io.modelcontextprotocol/clientCapabilities\":{}}";

    internal static void ConfigureModernHeaders(HttpRequestMessage request, string method)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", ModernProtocolVersion);
        request.Headers.Add("Mcp-Method", method);
    }
}
