// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Net.Http.Headers;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpRetryableHeaders
{
    public static void Apply(HttpContext context, McpJsonRpcResponse response)
    {
        var hint = ReadRetryabilityHint(response);
        if (hint is null)
        {
            return;
        }

        context.Response.Headers["Honua-Retryable"] = "true";
        if (hint.Value.RetryAfterSeconds is > 0 && !context.Response.Headers.ContainsKey(HeaderNames.RetryAfter))
        {
            context.Response.Headers.RetryAfter = hint.Value.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static RetryabilityHint? ReadRetryabilityHint(McpJsonRpcResponse response)
    {
        if (response.Error?.Data is { Retryable: true } errorData)
        {
            return new RetryabilityHint(errorData.RetryAfterSeconds);
        }

        return ReadToolErrorRetryabilityHint(response.Result);
    }

    private static RetryabilityHint? ReadToolErrorRetryabilityHint(JsonElement? resultElement)
    {
        if (resultElement is not { ValueKind: JsonValueKind.Object } result
            || !result.TryGetProperty("isError", out var isError)
            || isError.ValueKind != JsonValueKind.True
            || !result.TryGetProperty("structuredContent", out var structured)
            || structured.ValueKind != JsonValueKind.Object
            || !structured.TryGetProperty("retryable", out var retryable)
            || retryable.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        int? retryAfterSeconds = structured.TryGetProperty("retryAfterSeconds", out var delayElement)
            && delayElement.ValueKind == JsonValueKind.Number
            && delayElement.TryGetInt32(out var seconds)
            ? seconds
            : null;
        return new RetryabilityHint(retryAfterSeconds);
    }

    private readonly record struct RetryabilityHint(int? RetryAfterSeconds);
}
