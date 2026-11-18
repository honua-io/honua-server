// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;

namespace Honua.Server.Features.Admin;

// Tells MCP sessions that a committed layer publish changed the resource and tool lists.
// Synchronous and job publishes both call this, so an agent sees a new layer without reconnecting.
internal static class LayerPublishCatalogNotifications
{
    // Broadcasts list_changed once the publication has committed. A failed broadcast is reported
    // through onFailure and never turns the committed publish into an error.
    public static async Task BroadcastAsync(IServiceProvider services, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(onFailure);
        if (services.GetService<IMcpNotificationPublisher>() is not { } notifications)
        {
            return;
        }

        // Each list is announced on its own, so one failed broadcast does not hide the other.
        await TryBroadcastAsync(notifications.BroadcastResourcesListChanged, onFailure).ConfigureAwait(false);
        await TryBroadcastAsync(notifications.BroadcastToolsListChanged, onFailure).ConfigureAwait(false);
    }

    private static async Task TryBroadcastAsync(Func<ValueTask<int>> broadcast, Action<Exception> onFailure)
    {
        try
        {
            await broadcast().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            onFailure(exception);
        }
    }
}
