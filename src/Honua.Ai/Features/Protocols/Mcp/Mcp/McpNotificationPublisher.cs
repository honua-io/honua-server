// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Builds server-to-client MCP JSON-RPC notifications and enqueues them onto the
/// owning session's SSE stream (honua-server#1954). Centralizing the wire shape
/// here keeps the <c>notifications/progress</c> and <c>*/list_changed</c> frames
/// consistent and AOT-safe (source-generated serialization). A separate
/// <see cref="McpJobProgressBridge"/> sources progress from the canonical job
/// runtime and calls <see cref="PublishProgress"/>; the publish path calls the
/// broadcast methods when the catalog mutates.
/// </summary>
internal interface IMcpNotificationPublisher
{
    /// <summary>
    /// Pushes a <c>notifications/progress</c> frame to a single session. The
    /// <paramref name="progressToken"/> is the durable job id so a client can
    /// correlate the stream with the job it started. Returns <c>false</c> when the
    /// session is unknown/terminated (the frame is dropped).
    /// </summary>
    bool PublishProgress(string sessionId, string progressToken, double progress, double? total, string? message);

    /// <summary>
    /// Broadcasts <c>notifications/tools/list_changed</c> to every active session.
    /// Returns the number of sessions the frame was enqueued onto.
    /// </summary>
    int BroadcastToolsListChanged();

    /// <summary>
    /// Broadcasts <c>notifications/resources/list_changed</c> to every active
    /// session. Returns the number of sessions the frame was enqueued onto.
    /// </summary>
    int BroadcastResourcesListChanged();
}

/// <inheritdoc />
internal sealed class McpNotificationPublisher : IMcpNotificationPublisher
{
    /// <summary>JSON-RPC method for an MCP progress notification.</summary>
    public const string ProgressMethod = "notifications/progress";

    /// <summary>JSON-RPC method broadcast when the tool catalog changes.</summary>
    public const string ToolsListChangedMethod = "notifications/tools/list_changed";

    /// <summary>JSON-RPC method broadcast when the resource catalog changes.</summary>
    public const string ResourcesListChangedMethod = "notifications/resources/list_changed";

    private readonly McpSessionManager _sessions;
    private readonly ILogger<McpNotificationPublisher> _logger;

    public McpNotificationPublisher(McpSessionManager sessions, ILogger<McpNotificationPublisher> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool PublishProgress(string sessionId, string progressToken, double progress, double? total, string? message)
    {
        var parameters = new McpProgressNotificationParams
        {
            ProgressToken = progressToken,
            Progress = progress,
            Total = total,
            Message = message
        };

        var paramsJson = JsonSerializer.Serialize(parameters, McpJsonContext.Default.McpProgressNotificationParams);
        using var document = JsonDocument.Parse(paramsJson);
        var notification = Serialize(ProgressMethod, document.RootElement.Clone());
        var enqueued = _sessions.TryEnqueue(sessionId, notification);
        if (enqueued)
        {
            McpLog.NotificationPublished(_logger, ProgressMethod, sessionId);
        }

        return enqueued;
    }

    /// <inheritdoc />
    public int BroadcastToolsListChanged() => Broadcast(ToolsListChangedMethod);

    /// <inheritdoc />
    public int BroadcastResourcesListChanged() => Broadcast(ResourcesListChangedMethod);

    private int Broadcast(string method)
    {
        var notification = Serialize(method, parameters: null);
        var delivered = 0;
        foreach (var sessionId in _sessions.ActiveSessionIds)
        {
            if (_sessions.TryEnqueue(sessionId, notification))
            {
                delivered++;
            }
        }

        if (delivered > 0)
        {
            McpLog.NotificationBroadcast(_logger, method, delivered);
        }

        return delivered;
    }

    private static string Serialize(string method, JsonElement? parameters)
    {
        var notification = new McpJsonRpcNotification
        {
            Method = method,
            Params = parameters
        };

        return JsonSerializer.Serialize(notification, McpJsonContext.Default.McpJsonRpcNotification);
    }
}
