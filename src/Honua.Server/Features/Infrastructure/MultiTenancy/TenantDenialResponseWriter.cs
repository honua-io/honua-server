// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Tenant authorization denial causes that must retain their identity across protocol adapters.
/// </summary>
internal enum TenantDenialKind
{
    AuthenticationRequired,
    PermissionDenied,
    TenantSuspended,
    TenantDeleted,
}

/// <summary>
/// Writes tenant authorization failures in the request protocol's native wire contract.
/// </summary>
internal static class TenantDenialResponseWriter
{
    private const string CorrelationHeaderName = "X-Correlation-ID";
    private const int JsonRpcServerError = -32000;

    /// <summary>
    /// Terminates the request with a protocol-native denial envelope.
    /// </summary>
    internal static Task WriteAsync(HttpContext context, TenantDenialKind denial)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers[CorrelationHeaderName] = context.TraceIdentifier;
        var isMcpRequest = IsMcpRequest(context.Request.Path);
        var isMcpJsonRpcRequest = IsMcpJsonRpcRequest(context.Request);
        var isGrpcRequest = IsGrpcRequest(context.Request);
        if (denial == TenantDenialKind.AuthenticationRequired
            && !isMcpJsonRpcRequest
            && !isGrpcRequest)
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            if (isMcpRequest)
            {
                McpProtectedResourceMetadataEndpointExtensions.StampChallengeOnUnauthorized(context);
            }
        }

        if (isMcpJsonRpcRequest)
        {
            return WriteMcpAsync(context, denial);
        }

        if (isGrpcRequest)
        {
            WriteGrpc(context, denial);
            return Task.CompletedTask;
        }

        var statusCode = denial == TenantDenialKind.AuthenticationRequired
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;
        var machineCode = GetMachineCode(denial);
        var errorResponse = new StandardErrorResponse(
            statusCode,
            denial == TenantDenialKind.AuthenticationRequired ? "Authentication Required" : "Forbidden",
            GetMessage(denial),
            [$"CorrelationId: {context.TraceIdentifier}"]);
        var options = new ErrorResponseFormatterOptions
        {
            MachineCode = machineCode,
            ODataErrorCode = machineCode,
            WfsExceptionCode = GetXmlCode(denial),
            WcsExceptionCode = GetXmlCode(denial),
            WpsExceptionCode = GetXmlCode(denial),
            WmtsExceptionCode = GetXmlCode(denial),
            WmsExceptionCode = GetXmlCode(denial),
            GeoServicesBodyCode = denial == TenantDenialKind.AuthenticationRequired
                ? GeoServicesErrorCodes.TokenRequired
                : StatusCodes.Status403Forbidden,
        };

        return StandardErrorResponseFormatter.WriteErrorAsync(context, errorResponse, options);
    }

    /// <summary>
    /// Identifies MCP calls that cross the tenant data boundary while preserving tenantless discovery.
    /// </summary>
    internal static async Task<bool> IsTenantBoundMcpRequestAsync(HttpRequest request)
    {
        if (!IsMcpRequest(request.Path))
        {
            return false;
        }

        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
        {
            return true;
        }

        if (!HttpMethods.IsPost(request.Method) || request.ContentLength == 0)
        {
            return false;
        }

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(
                    request.Body,
                    cancellationToken: request.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                ? IsTenantBoundMcpMethod(root)
                : root.ValueKind == JsonValueKind.Array && root.EnumerateArray().Any(IsTenantBoundMcpMethod);
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static async Task WriteMcpAsync(HttpContext context, TenantDenialKind denial)
    {
        var request = await ReadMcpRequestEnvelopeAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (request.ResponseIds.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            if (request.IsBatch)
            {
                writer.WriteStartArray();
            }

            foreach (var id in request.ResponseIds)
            {
                WriteMcpError(writer, id, denial, context.TraceIdentifier);
            }

            if (request.IsBatch)
            {
                writer.WriteEndArray();
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(output.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    private static void WriteMcpError(
        Utf8JsonWriter writer,
        JsonElement? id,
        TenantDenialKind denial,
        string correlationId)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id.HasValue)
        {
            id.Value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteStartObject("error");
        writer.WriteNumber("code", JsonRpcServerError);
        writer.WriteString("message", GetMessage(denial));
        writer.WriteStartObject("data");
        writer.WriteString("code", GetMachineCode(denial));
        writer.WriteString("correlationId", correlationId);
        if (denial == TenantDenialKind.AuthenticationRequired)
        {
            writer.WriteBoolean("requiresReauthentication", true);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static async Task<McpRequestEnvelope> ReadMcpRequestEnvelopeAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength == 0)
        {
            return SingleMcpError();
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return ClassifyMcpEnvelope(document.RootElement);
        }
        catch (JsonException)
        {
            return SingleMcpError();
        }
    }

    private static McpRequestEnvelope ClassifyMcpEnvelope(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
            {
                return SingleMcpError();
            }

            var responseIds = new List<JsonElement?>();
            foreach (var element in root.EnumerateArray())
            {
                if (TryGetMcpResponseId(element, out var id))
                {
                    responseIds.Add(id);
                }
            }

            return new McpRequestEnvelope(IsBatch: true, responseIds);
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            return TryGetMcpResponseId(root, out var id)
                ? new McpRequestEnvelope(IsBatch: false, new JsonElement?[] { id })
                : new McpRequestEnvelope(IsBatch: false, Array.Empty<JsonElement?>());
        }

        return SingleMcpError();
    }

    private static bool TryGetMcpResponseId(JsonElement message, out JsonElement? responseId)
    {
        responseId = null;
        if (message.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var hasId = message.TryGetProperty("id", out var id);
        if (!hasId && IsMcpNotification(message))
        {
            return false;
        }

        if (hasId && IsValidMcpRequestId(id))
        {
            responseId = id.Clone();
        }

        return true;
    }

    private static bool IsMcpNotification(JsonElement message)
        => message.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String
            && method.GetString()?.StartsWith("notifications/", StringComparison.Ordinal) == true;

    private static bool IsValidMcpRequestId(JsonElement id)
        => id.ValueKind == JsonValueKind.String
            || id.ValueKind == JsonValueKind.Number && IsIntegerNumberToken(id);

    private static bool IsIntegerNumberToken(JsonElement element)
    {
        var raw = element.GetRawText().AsSpan();
        if (raw.Length == 0)
        {
            return false;
        }

        var start = raw[0] == '-' ? 1 : 0;
        if (start == raw.Length)
        {
            return false;
        }

        for (var index = start; index < raw.Length; index++)
        {
            if (raw[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTenantBoundMcpMethod(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var method = methodElement.GetString();
        return string.Equals(method, "tools/call", StringComparison.Ordinal)
            || string.Equals(method, "resources/read", StringComparison.Ordinal);
    }

    private static McpRequestEnvelope SingleMcpError()
        => new(IsBatch: false, new JsonElement?[] { null });

    private static bool IsMcpJsonRpcRequest(HttpRequest request)
        => HttpMethods.IsPost(request.Method) && IsMcpRequest(request.Path);

    private static void WriteGrpc(HttpContext context, TenantDenialKind denial)
    {
        var status = denial == TenantDenialKind.AuthenticationRequired ? 16 : 7;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/grpc";
        context.Response.Headers["grpc-status"] = status.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["grpc-message"] = Uri.EscapeDataString(GetMessage(denial));
        context.Response.Headers["honua-error-code"] = GetMachineCode(denial);
        context.Response.Headers["correlation-id"] = context.TraceIdentifier;
    }

    private static bool IsMcpRequest(PathString path)
        => path.Equals("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/mcp/", StringComparison.OrdinalIgnoreCase);

    private static bool IsGrpcRequest(HttpRequest request)
        => request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record McpRequestEnvelope(bool IsBatch, IReadOnlyList<JsonElement?> ResponseIds);

    private static string GetMachineCode(TenantDenialKind denial) => denial switch
    {
        TenantDenialKind.AuthenticationRequired => "authentication_required",
        TenantDenialKind.PermissionDenied => "permission_denied",
        TenantDenialKind.TenantSuspended => "tenant_suspended",
        TenantDenialKind.TenantDeleted => "tenant_deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(denial), denial, null),
    };

    private static string GetXmlCode(TenantDenialKind denial) => denial switch
    {
        TenantDenialKind.AuthenticationRequired => "AuthenticationRequired",
        TenantDenialKind.PermissionDenied => "AccessDenied",
        TenantDenialKind.TenantSuspended => "TenantSuspended",
        TenantDenialKind.TenantDeleted => "TenantDeleted",
        _ => throw new ArgumentOutOfRangeException(nameof(denial), denial, null),
    };

    private static string GetMessage(TenantDenialKind denial) => denial switch
    {
        TenantDenialKind.AuthenticationRequired => "Authentication with a tenant claim is required.",
        TenantDenialKind.PermissionDenied => "The caller is not authorized to select this tenant.",
        TenantDenialKind.TenantSuspended => "Tenant access is currently suspended.",
        TenantDenialKind.TenantDeleted => "Tenant access is unavailable.",
        _ => throw new ArgumentOutOfRangeException(nameof(denial), denial, null),
    };
}
