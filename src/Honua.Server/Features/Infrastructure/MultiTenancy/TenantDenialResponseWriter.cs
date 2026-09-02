// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text.Json;
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
        if (IsMcpRequest(context.Request.Path))
        {
            return WriteMcpAsync(context, denial);
        }

        if (IsGrpcRequest(context.Request))
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
        if (!IsMcpRequest(request.Path) || request.ContentLength == 0)
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
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var method = methodElement.GetString();
            return string.Equals(method, "tools/call", StringComparison.Ordinal)
                || string.Equals(method, "resources/read", StringComparison.Ordinal);
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
        var requestId = await ReadMcpRequestIdAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (requestId.HasValue)
            {
                requestId.Value.WriteTo(writer);
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
            writer.WriteString("correlationId", context.TraceIdentifier);
            if (denial == TenantDenialKind.AuthenticationRequired)
            {
                writer.WriteBoolean("requiresReauthentication", true);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(output.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<JsonElement?> ReadMcpRequestIdAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength == 0)
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id)
                    ? id.Clone()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
