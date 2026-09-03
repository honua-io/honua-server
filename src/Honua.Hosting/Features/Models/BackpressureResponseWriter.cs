// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
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
    private const int MaxMcpInspectionBytes = 16 * 1024;
    private const string EventStreamMimeType = "text/event-stream";
    private static readonly byte[] SsePrefix = "event: message\ndata: "u8.ToArray();
    private static readonly byte[] SseSuffix = "\n\n"u8.ToArray();

    internal static async Task WriteAsync(
        HttpContext context,
        BackpressureKind kind,
        int? retryAfterSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        AddTransportHeaders(context, retryAfterSeconds);

        if (IsMcpPost(context))
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
        var request = await InspectMcpRequestAsync(context.Request, cancellationToken).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status200OK;
        var eventStream = AcceptsEventStream(context);
        ArrayBufferWriter<byte>? buffered = eventStream ? new ArrayBufferWriter<byte>() : null;
        IBufferWriter<byte> output = eventStream
            ? (IBufferWriter<byte>)buffered!
            : context.Response.BodyWriter;

        if (eventStream)
        {
            context.Response.ContentType = EventStreamMimeType;
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
        }

        await using var writer = new Utf8JsonWriter(output);
        if (request.IsBatch)
        {
            writer.WriteStartArray();
            foreach (var id in request.Ids.Count == 0 ? new JsonElement?[] { null } : request.Ids)
            {
                WriteMcpError(writer, id, kind, retryAfterSeconds, context.TraceIdentifier);
            }

            writer.WriteEndArray();
        }
        else
        {
            WriteMcpError(
                writer,
                request.Ids.Count == 0 ? null : request.Ids[0],
                kind,
                retryAfterSeconds,
                context.TraceIdentifier);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (buffered is not null)
        {
            await context.Response.Body.WriteAsync(SsePrefix, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.WriteAsync(buffered.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.WriteAsync(SseSuffix, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteMcpError(
        Utf8JsonWriter writer,
        JsonElement? id,
        BackpressureKind kind,
        int? retryAfterSeconds,
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

        writer.WriteString("correlationId", correlationId);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
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

    private static async Task<McpRequestInspection> InspectMcpRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        var buffer = ArrayPool<byte>.Shared.Rent(MaxMcpInspectionBytes);
        var bytesRead = 0;
        var isBatch = false;
        try
        {
            bytesRead = await request.Body.ReadAtLeastAsync(
                buffer.AsMemory(0, MaxMcpInspectionBytes),
                minimumBytes: MaxMcpInspectionBytes,
                throwOnEndOfStream: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            isBatch = IsJsonArray(buffer.AsSpan(0, bytesRead));
            var ids = new List<JsonElement?>();
            try
            {
                using var document = JsonDocument.Parse(buffer.AsMemory(0, bytesRead));
                if (isBatch && document.RootElement.ValueKind == JsonValueKind.Array)
                    ids.AddRange(document.RootElement.EnumerateArray().Select(ReadRequestId));
                else if (!isBatch && document.RootElement.ValueKind == JsonValueKind.Object)
                    ids.Add(ReadRequestId(document.RootElement));

                return new McpRequestInspection(isBatch, ids);
            }
            catch (JsonException)
            {
                return new McpRequestInspection(
                    isBatch,
                    ReadRequestIdsFromPrefix(buffer.AsSpan(0, bytesRead), isBatch));
            }
        }
        catch (JsonException)
        {
            // The backpressure contract still uses a valid JSON-RPC response with a null id.
            return new McpRequestInspection(isBatch, []);
        }
        finally
        {
            request.Body.Position = 0;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JsonElement? ReadRequestId(JsonElement request)
        => request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var id)
            ? id.Clone()
            : null;

    private static List<JsonElement?> ReadRequestIdsFromPrefix(
        ReadOnlySpan<byte> payload,
        bool isBatch)
    {
        var ids = new List<JsonElement?>();
        var reader = new Utf8JsonReader(payload, isFinalBlock: false, state: default);

        try
        {
            if (!reader.Read())
            {
                return ids;
            }

            if (!isBatch)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    return ids;
                }

                var hasId = false;
                JsonElement? id = null;
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName
                        || reader.CurrentDepth != 1
                        || !reader.ValueTextEquals("id"u8))
                    {
                        continue;
                    }

                    if (!reader.Read() || !TryReadJsonValue(ref reader, out var value))
                    {
                        break;
                    }

                    id = value;
                    hasId = true;
                }

                if (hasId)
                {
                    ids.Add(id);
                }

                return ids;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return ids;
            }

            var activeItem = false;
            var activeItemHasId = false;
            JsonElement? activeItemId = null;
            while (reader.Read())
            {
                if (!activeItem && reader.CurrentDepth == 1)
                {
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        activeItem = true;
                        activeItemHasId = false;
                        activeItemId = null;
                    }
                    else if (reader.TokenType is not JsonTokenType.EndArray)
                    {
                        ids.Add(null);
                    }

                    continue;
                }

                if (activeItem
                    && reader.CurrentDepth == 2
                    && reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals("id"u8))
                {
                    if (!reader.Read() || !TryReadJsonValue(ref reader, out var value))
                    {
                        break;
                    }

                    activeItemId = value;
                    activeItemHasId = true;
                    continue;
                }

                if (activeItem
                    && reader.CurrentDepth == 1
                    && reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    ids.Add(activeItemHasId ? activeItemId : null);
                    activeItem = false;
                }
            }

            // A bounded prefix may end inside the final item. A complete id property
            // is still enough to correlate that visible request in the denial envelope.
            if (activeItem && activeItemHasId)
            {
                ids.Add(activeItemId);
            }
        }
        catch (JsonException)
        {
            // Keep IDs already observed before malformed or incomplete input.
        }

        return ids;
    }

    private static bool TryReadJsonValue(ref Utf8JsonReader reader, out JsonElement value)
    {
        try
        {
            using var document = JsonDocument.ParseValue(ref reader);
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static bool IsJsonArray(ReadOnlySpan<byte> payload)
    {
        var index = 0;
        while (index < payload.Length && char.IsWhiteSpace((char)payload[index]))
        {
            index++;
        }

        return index < payload.Length && payload[index] == (byte)'[';
    }

    private static bool AcceptsEventStream(HttpContext context)
        => context.Request.Headers.TryGetValue("Accept", out var accept) && accept.ToString().Split(',')
            .Select(media => media.Split(';', 2)[0].Trim())
            .Any(token => token.Equals(EventStreamMimeType, StringComparison.OrdinalIgnoreCase));

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

    private static bool IsMcpPost(HttpContext context)
        => HttpMethods.IsPost(context.Request.Method)
            && (context.Request.Path.Equals(new PathString("/mcp"))
                || context.Request.Path.Equals(new PathString("/mcp/")));

    private static bool IsGrpc(HttpContext context)
        => context.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true
            || context.Request.Path.StartsWithSegments("/honua.v1", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/geospatial.v1", StringComparison.OrdinalIgnoreCase);

    private sealed record McpRequestInspection(bool IsBatch, IReadOnlyList<JsonElement?> Ids);
}

/// <summary>
/// The transient server condition represented by a native backpressure response.
/// </summary>
internal enum BackpressureKind
{
    Throttled,
    Saturated,
}
