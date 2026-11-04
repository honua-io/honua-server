// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpModernProtocol
{
    internal const string Version = "2026-07-28";
    internal static readonly IReadOnlyList<string> SupportedVersions = [Version];
    internal const string VersionHeader = "MCP-Protocol-Version";
    internal const string VersionMetadata = "io.modelcontextprotocol/protocolVersion";
    internal const string ClientInfoMetadata = "io.modelcontextprotocol/clientInfo";
    internal const string CapabilitiesMetadata = "io.modelcontextprotocol/clientCapabilities";
    private const int HeaderMismatchErrorCode = -32020;
    private const int UnsupportedVersionErrorCode = -32022;
    private const string EncodedPrefix = "=?base64?";
    private const string EncodedSuffix = "?=";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static readonly object RequestItemKey = new();

    internal static bool HasModernHeader(HttpRequest request)
    {
        var version = request.Headers[VersionHeader].ToString();
        return version.Length > 0 && string.CompareOrdinal(version, Version) >= 0;
    }

    internal static bool AppliesTo(HttpContext context, JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Array)
        {
            return HasModernHeader(context.Request) || body.EnumerateArray().Any(item => AppliesTo(context, item));
        }

        var hasProtocolMetadata = body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && (meta.TryGetProperty(VersionMetadata, out _)
                || meta.TryGetProperty(CapabilitiesMetadata, out _)
                || meta.TryGetProperty(ClientInfoMetadata, out _));
        if (body.ValueKind == JsonValueKind.Object && ReadString(body, "method") == "initialize" && !hasProtocolMetadata)
        {
            return false;
        }

        return hasProtocolMetadata || HasModernHeader(context.Request)
            || (body.ValueKind == JsonValueKind.Object && ReadString(body, "method") == "server/discover");
    }

    internal static McpJsonRpcError? Validate(HttpRequest request, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Error(McpErrorMapper.JsonRpcInvalidRequest, "Modern MCP requires a single JSON-RPC object.");
        }

        var version = ReadValidatedVersion(body);
        if (version is null)
        {
            return Error(McpErrorMapper.JsonRpcInvalidParams, "Request metadata requires protocolVersion, clientCapabilities and valid optional clientInfo.");
        }

        var headerError = ValidateHeaders(request, body, version);
        if (headerError is not null)
        {
            return headerError;
        }

        return version == Version ? null : new McpJsonRpcError
        {
            Code = UnsupportedVersionErrorCode,
            Message = "Unsupported protocol version.",
            Data = new McpErrorData { Code = "unsupported_protocol_version", Supported = SupportedVersions, Requested = version }
        };
    }

    private static string? ReadValidatedVersion(JsonElement body)
    {
        if (!body.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("_meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || ReadString(meta, VersionMetadata) is not { Length: > 0 } version
            || !meta.TryGetProperty(CapabilitiesMetadata, out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (meta.TryGetProperty(ClientInfoMetadata, out var info)
            && (info.ValueKind != JsonValueKind.Object || ReadString(info, "name") is null || ReadString(info, "version") is null))
        {
            return null;
        }

        return version;
    }

    private static McpJsonRpcError? ValidateHeaders(HttpRequest request, JsonElement body, string version)
    {
        if (!MatchesHeader(request, VersionHeader, version)
            || !MatchesHeader(request, "Mcp-Method", ReadString(body, "method")))
        {
            return Error(HeaderMismatchErrorCode, "Required protocol or method header is missing or does not match the body.");
        }

        var method = ReadString(body, "method");
        if (method is not ("tools/call" or "resources/read" or "prompts/get"))
        {
            return null;
        }

        var parameters = body.GetProperty("params");
        var name = ReadString(parameters, method == "resources/read" ? "uri" : "name");
        return MatchesHeader(request, "Mcp-Name", name) ? null
            : Error(HeaderMismatchErrorCode, "Required name header is missing or does not match the body.");
    }

    private static bool MatchesHeader(HttpRequest request, string name, string? expected)
    {
        if (expected is null || !request.Headers.TryGetValue(name, out var values) || values.Count != 1)
        {
            return false;
        }

        var value = values[0]!;
        if (value.Length == 0 || value.Any(character => character is < ' ' or > '~')
            || value.Trim() != value)
        {
            return false;
        }

        if (name == "Mcp-Name" && value.StartsWith(EncodedPrefix, StringComparison.Ordinal)
            && value.EndsWith(EncodedSuffix, StringComparison.Ordinal))
        {
            try
            {
                value = StrictUtf8.GetString(Convert.FromBase64String(value[EncodedPrefix.Length..^EncodedSuffix.Length]));
            }
            catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
            {
                return false;
            }
        }

        return string.Equals(value, expected, StringComparison.Ordinal);
    }

    internal static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static McpJsonRpcError Error(int code, string message) => new() { Code = code, Message = message };
}
