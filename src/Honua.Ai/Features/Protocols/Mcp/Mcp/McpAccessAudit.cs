// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp;

// One durable record per tool call and resource read by an authenticated principal, so an
// operator can review what an agent session read. Records carry identifiers only, never query
// text, arguments outside the identifier list, or results. A failed write is logged and never
// fails the call it describes.
internal static class McpAccessAudit
{
    public const string ResourceType = "mcp";
    public const string ToolCallAction = "mcp.tool.call";
    public const string ResourceReadAction = "mcp.resource.read";

    private const int MaxTargetValueLength = 128;

    private static readonly string[] TargetArgumentNames =
    [
        "serviceId", "serviceLayerIndex", "layerId", "styleId", "jobId", "sourceId", "connectionId",
        "folderId", "itemId", "versionId", "draftId", "mapPackageId", "appPackageId", "datasetId",
    ];

    public static McpAccess ToolCall(string toolName, JsonElement? arguments, string? errorCode) => new()
    {
        Action = ToolCallAction,
        ResourceId = toolName,
        Details = ToolDetails(arguments, errorCode),
        ErrorCode = errorCode,
    };

    public static McpAccess ResourceRead(string uri, string? errorCode) => new()
    {
        Action = ResourceReadAction,
        ResourceId = ResourcePath(uri),
        Details = CodeDetails(errorCode),
        ErrorCode = errorCode,
    };

    public static AuditOutcome Outcome(string? errorCode) => errorCode switch
    {
        null => AuditOutcome.Success,
        McpErrorMapper.Codes.Unauthenticated
            or McpErrorMapper.Codes.PermissionDenied
            or McpErrorMapper.Codes.InsufficientScope => AuditOutcome.Denied,
        _ => AuditOutcome.Failure,
    };

    // The query and fragment can carry caller text; the path names the resource.
    public static string ResourcePath(string uri)
    {
        var end = uri.IndexOfAny(['?', '#']);
        return end < 0 ? uri : uri[..end];
    }

    public static string ToolDetails(JsonElement? arguments, string? errorCode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("targets");
            if (arguments is { ValueKind: JsonValueKind.Object } supplied)
            {
                foreach (var name in TargetArgumentNames)
                {
                    WriteTarget(writer, supplied, name);
                }
            }

            writer.WriteEndObject();
            WriteCode(writer, errorCode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string CodeDetails(string? errorCode)
    {
        if (errorCode is null)
        {
            return string.Empty;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteCode(writer, errorCode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTarget(Utf8JsonWriter writer, JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            writer.WriteNumber(name, number);
        }
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            writer.WriteString(name, text.Length > MaxTargetValueLength ? text[..MaxTargetValueLength] : text);
        }
    }

    private static void WriteCode(Utf8JsonWriter writer, string? errorCode)
    {
        if (errorCode is not null)
        {
            writer.WriteString("code", errorCode);
        }
    }

    public static async Task RecordAsync(HttpContext context, McpAccess access, ILogger logger)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.RequestServices.GetService<IAuditLog>() is not { } auditLog)
        {
            return;
        }

        try
        {
            await auditLog.RecordAsync(ToAuditEvent(context, access), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            McpLog.AccessAuditFailed(logger, access.Action, access.ResourceId, ex);
        }
    }

    private static AuditEvent ToAuditEvent(HttpContext context, McpAccess access) => new()
    {
        Timestamp = (context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(),
        EventType = AuditEventType.DataExport,
        Actor = AuditContextResolver.ResolveActor(context, out var actorType),
        ActorType = actorType,
        ActorAgent = AuditContextResolver.ResolveAgent(context),
        ResourceType = ResourceType,
        ResourceId = access.ResourceId,
        Action = access.Action,
        Outcome = Outcome(access.ErrorCode),
        CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
        RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
        UserAgent = AuditContextResolver.ResolveUserAgent(context),
        Details = access.Details,
    };
}

// What one audited MCP access names: the action, the tool or resource path, identifier-only
// details, and the error code when the access failed.
internal sealed class McpAccess
{
    public required string Action { get; init; }

    public required string ResourceId { get; init; }

    public required string Details { get; init; }

    public string? ErrorCode { get; init; }
}
