// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Backpressure;

namespace Honua.Infrastructure.Models;

/// <summary>
/// Writes retryable throttling and saturation failures in the request protocol's native envelope.
/// </summary>
internal static class BackpressureResponseWriter
{
    private const int JsonRpcServerError = -32000;

    internal static async Task WriteAsync(
        HttpContext context,
        BackpressureKind kind,
        int? retryAfterSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        AddTransportHeaders(context, retryAfterSeconds);

        if (IsMcp(context.Request.Path))
        {
            await WriteMcpAsync(context, kind, retryAfterSeconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsGrpc(context))
        {
            WriteGrpc(context, kind, retryAfterSeconds);
            return;
        }

        var error = kind == BackpressureKind.Throttled
            ? StandardErrorResponse.TooManyRequests("Too many requests. Please try again later.", retryAfterSeconds)
            : StandardErrorResponse.ServiceUnavailable("The service is temporarily unavailable. Please try again later.", retryAfterSeconds);
        var machineCode = GetMachineCode(kind);
        var options = new ErrorResponseFormatterOptions
        {
            AdditionalHeaders = BuildHeaders(context, retryAfterSeconds),
            MachineCode = machineCode,
            ODataErrorCode = machineCode,
            WfsExceptionCode = machineCode,
            WmsExceptionCode = machineCode,
            Retryable = true,
            RetryAfterSeconds = retryAfterSeconds,
        };

        await StandardErrorResponseFormatter.WriteErrorAsync(context, error, options).ConfigureAwait(false);
    }

    private static async Task WriteMcpAsync(
        HttpContext context,
        BackpressureKind kind,
        int? retryAfterSeconds,
        CancellationToken cancellationToken)
    {
        var id = await ReadMcpRequestIdAsync(context.Request, cancellationToken).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);
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
        writer.WriteString(
            "message",
            kind == BackpressureKind.Throttled
                ? "Request rate limit exceeded. Retry after the advised delay."
                : "The service is temporarily unavailable. Retry after the advised delay.");
        writer.WriteStartObject("data");
        writer.WriteString("code", kind == BackpressureKind.Throttled ? "resource_exhausted" : "unavailable");
        writer.WriteBoolean("retryable", true);
        if (retryAfterSeconds.HasValue)
        {
            writer.WriteNumber("retryAfterSeconds", retryAfterSeconds.Value);
        }

        writer.WriteString("correlationId", context.TraceIdentifier);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteGrpc(HttpContext context, BackpressureKind kind, int? retryAfterSeconds)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/grpc";
        context.Response.ContentLength = 0;
        context.Response.Headers["grpc-status"] = kind == BackpressureKind.Throttled ? "8" : "14";
        context.Response.Headers["grpc-message"] = kind == BackpressureKind.Throttled
            ? "Request rate limit exceeded"
            : "Service temporarily unavailable";
        context.Response.Headers[BackpressureMetadata.ErrorCodeKey] = GetMachineCode(kind);
        context.Response.Headers[BackpressureMetadata.RetryableKey] = "true";
        context.Response.Headers[BackpressureMetadata.CorrelationIdKey] = context.TraceIdentifier;
        if (retryAfterSeconds.HasValue)
        {
            context.Response.Headers[BackpressureMetadata.RetryAfterKey] =
                retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static async Task<JsonElement?> ReadMcpRequestIdAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var id))
            {
                return id.Clone();
            }
        }
        catch (JsonException)
        {
            // The backpressure contract still uses a valid JSON-RPC response with a null id.
        }
        finally
        {
            request.Body.Position = 0;
        }

        return null;
    }

    private static void AddTransportHeaders(HttpContext context, int? retryAfterSeconds)
    {
        foreach (var header in BuildHeaders(context, retryAfterSeconds))
        {
            context.Response.Headers[header.Key] = header.Value;
        }
    }

    private static Dictionary<string, string> BuildHeaders(HttpContext context, int? retryAfterSeconds)
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Correlation-ID"] = context.TraceIdentifier,
            ["Honua-Retryable"] = "true",
        };

        if (retryAfterSeconds.HasValue)
        {
            headers["Retry-After"] = retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        return headers;
    }

    private static string GetMachineCode(BackpressureKind kind) => kind == BackpressureKind.Throttled
        ? BackpressureMetadata.RateLimitExceededCode
        : BackpressureMetadata.ServiceUnavailableCode;

    private static bool IsMcp(PathString path)
        => path.Equals(new PathString("/mcp"));

    private static bool IsGrpc(HttpContext context)
        => context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true
            || context.Request.Path.StartsWithSegments("/honua.v1", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/geospatial.v1", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The transient server condition represented by a native backpressure response.
/// </summary>
internal enum BackpressureKind
{
    Throttled,
    Saturated,
}
