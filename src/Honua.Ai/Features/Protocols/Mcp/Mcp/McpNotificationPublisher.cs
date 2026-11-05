// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

public interface IMcpNotificationPublisher
{
    bool PublishProgress(string sessionId, string progressToken, double progress, double? total, string? message);

    int BroadcastToolsListChanged();

    int BroadcastResourcesListChanged();
}

internal sealed class McpNotificationPublisher : IMcpNotificationPublisher
{
    public const string ProgressMethod = "notifications/progress";

    public const string ToolsListChangedMethod = "notifications/tools/list_changed";

    public const string ResourcesListChangedMethod = "notifications/resources/list_changed";

    private readonly McpSessionManager _sessions;
    private readonly ILogger<McpNotificationPublisher> _logger;

    public McpNotificationPublisher(McpSessionManager sessions, ILogger<McpNotificationPublisher> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

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

    public int BroadcastToolsListChanged() => Broadcast(ToolsListChangedMethod);

    public int BroadcastResourcesListChanged() => Broadcast(ResourcesListChangedMethod);

    private int Broadcast(string method)
    {
        var notification = Serialize(method, parameters: null);
        var delivered = 0;
        foreach (var sessionId in _sessions.ActiveSessionIds)
        {
            delivered += _sessions.TryEnqueue(sessionId, notification) ? 1 : 0;
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
