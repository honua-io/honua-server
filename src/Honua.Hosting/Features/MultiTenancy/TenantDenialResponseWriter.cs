// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;

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
        if (denial == TenantDenialKind.AuthenticationRequired)
        {
            AddAuthenticationChallenge(context);
        }

        if (IsMcpJsonRpcRequest(context.Request))
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
            WcsExceptionCode = GetXmlCode(denial),
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
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray().Any(IsTenantBoundMcpElement);
            }

            return IsTenantBoundMcpElement(document.RootElement);
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
        var request = await ReadMcpRequestIdentityAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (request.IsBatch)
        {
            await WriteMcpBatchAsync(context, denial, request.BatchItems!).ConfigureAwait(false);
            return;
        }

        if (request.IsNotification)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var output = BuildMcpError(request.Id, context, denial);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(output, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteMcpBatchAsync(
        HttpContext context,
        TenantDenialKind denial,
        IReadOnlyList<McpBatchItem> items)
    {
        if (items.Count == 0)
        {
            var emptyBatchError = BuildMcpError(null, context, denial);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(emptyBatchError, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var responseItems = items.Where(static item => !item.IsNotification).ToArray();
        if (responseItems.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (var item in responseItems)
            {
                using var itemDocument = JsonDocument.Parse(BuildMcpError(item.Id, context, denial));
                itemDocument.RootElement.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(output.WrittenMemory, context.RequestAborted).ConfigureAwait(false);
    }

    private static byte[] BuildMcpError(JsonElement? id, HttpContext context, TenantDenialKind denial)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
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
            writer.WriteString("correlationId", context.TraceIdentifier);
            if (denial == TenantDenialKind.AuthenticationRequired)
            {
                writer.WriteBoolean("requiresReauthentication", true);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return output.WrittenMemory.ToArray();
    }

    private static async Task<McpRequestIdentity> ReadMcpRequestIdentityAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength == 0)
        {
            return default;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var items = document.RootElement.EnumerateArray()
                    .Select(ParseMcpBatchItem)
                    .ToArray();
                return new McpRequestIdentity(null, false, true, items);
            }

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ParseMcpRequestIdentity(document.RootElement)
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool IsMcpJsonRpcRequest(HttpRequest request)
        => HttpMethods.IsPost(request.Method) && IsMcpRequest(request.Path);

    private static McpRequestIdentity ParseMcpRequestIdentity(JsonElement element)
    {
        var hasId = element.TryGetProperty("id", out var id);
        var method = element.TryGetProperty("method", out var methodElement)
            && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()
            : null;
        var isNotification = !hasId
            && method?.StartsWith("notifications/", StringComparison.Ordinal) == true;
        JsonElement? normalizedId = hasId && IsValidMcpRequestId(id) ? id.Clone() : null;
        return new McpRequestIdentity(normalizedId, isNotification, false, null);
    }

    private static McpBatchItem ParseMcpBatchItem(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new McpBatchItem(null, false);
        }

        var identity = ParseMcpRequestIdentity(element);
        return new McpBatchItem(identity.Id, identity.IsNotification);
    }

    private static bool IsTenantBoundMcpElement(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String
            && (string.Equals(method.GetString(), "tools/call", StringComparison.Ordinal)
                || string.Equals(method.GetString(), "resources/read", StringComparison.Ordinal));

    private static bool IsValidMcpRequestId(JsonElement id)
    {
        if (id.ValueKind == JsonValueKind.String)
        {
            return true;
        }

        if (id.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = id.GetRawText().AsSpan();
        var start = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
        return start < raw.Length && raw[start..].IndexOfAny('.', 'e', 'E') < 0;
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

    private readonly record struct McpRequestIdentity(
        JsonElement? Id,
        bool IsNotification,
        bool IsBatch,
        IReadOnlyList<McpBatchItem>? BatchItems);

    private readonly record struct McpBatchItem(JsonElement? Id, bool IsNotification);

    private static void AddAuthenticationChallenge(HttpContext context)
    {
        const string bearerChallenge = "Bearer";
        var challenge = bearerChallenge;
        if (IsMcpRequest(context.Request.Path))
        {
            var oidc = context.RequestServices.GetService<IOptions<OidcAuthenticationOptions>>()?.Value;
            if (oidc is { Enabled: true }
                && OidcProviderCatalog.GetProviders(oidc).Any(static provider =>
                    provider.IsValid && !string.IsNullOrWhiteSpace(provider.Authority)))
            {
                var metadata = $"{BaseUrlResolver.GetBaseUrl(context).TrimEnd('/')}/.well-known/oauth-protected-resource/mcp";
                challenge = $"{bearerChallenge} resource_metadata=\"{metadata}\"";
            }
        }

        context.Response.Headers.Append("WWW-Authenticate", challenge);
    }

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
