// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Core.Features.Realtime;

namespace Honua.Ai.Protocols.Mcp;

public interface IMcpNotificationPublisher
{
    ValueTask<bool> PublishProgress(string sessionId, string progressToken, double progress, double? total, string? message);

    ValueTask<int> BroadcastToolsListChanged();

    ValueTask<int> BroadcastResourcesListChanged();
}

internal sealed class McpNotificationPublisher : IMcpNotificationPublisher
{
    public const string ProgressMethod = "notifications/progress";

    public const string ToolsListChangedMethod = "notifications/tools/list_changed";

    public const string ResourcesListChangedMethod = "notifications/resources/list_changed";

    private readonly McpSessionManager _sessions;
    private readonly IRealtimeFanoutProvider? _provider;
    private readonly ILogger<McpNotificationPublisher> _logger;

    public McpNotificationPublisher(McpSessionManager sessions, ILogger<McpNotificationPublisher> logger, IRealtimeFanoutProvider? provider = null)
    {
        _sessions = sessions;
        _provider = provider;
        _logger = logger;
    }

    public async ValueTask<bool> PublishProgress(string sessionId, string progressToken, double progress, double? total, string? message)
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
        if (_provider is null)
        {
            var enqueued = _sessions.TryEnqueue(sessionId, notification);
            if (enqueued)
            {
                McpLog.NotificationPublished(_logger, ProgressMethod, sessionId);
            }

            return enqueued;
        }

        await AppendAsync(sessionId, ProgressMethod, notification).ConfigureAwait(false);
        McpLog.NotificationPublished(_logger, ProgressMethod, sessionId);
        return true;
    }

    public ValueTask<int> BroadcastToolsListChanged() => BroadcastAsync(ToolsListChangedMethod);

    public ValueTask<int> BroadcastResourcesListChanged() => BroadcastAsync(ResourcesListChangedMethod);

    private async ValueTask<int> BroadcastAsync(string method)
    {
        var notification = Serialize(method, parameters: null);
        int localSessions;
        if (_provider is null)
        {
            localSessions = 0;
            foreach (var sessionId in _sessions.ActiveSessionIds)
            {
                if (_sessions.TryEnqueue(sessionId, notification))
                {
                    localSessions++;
                }
            }
        }
        else
        {
            await AppendAsync("*", method, notification).ConfigureAwait(false);
            localSessions = _sessions.ActiveSessionIds.Count;
        }

        McpLog.NotificationBroadcast(_logger, method, localSessions);
        return localSessions;
    }

    private async ValueTask AppendAsync(string scope, string method, string notification)
    {
        using var document = JsonDocument.Parse(notification);
        await _provider!.AppendAsync(
            new RealtimeEventDraft(RealtimeFamily.Mcp, scope, method, document.RootElement.Clone()),
            null, CancellationToken.None).ConfigureAwait(false);
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
