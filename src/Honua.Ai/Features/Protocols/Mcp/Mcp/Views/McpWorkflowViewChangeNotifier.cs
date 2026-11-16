// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Views;

internal sealed class McpWorkflowViewChangeNotifier(
    IOptionsMonitor<McpWorkflowViewOptions> options,
    IMcpNotificationPublisher publisher,
    ILogger<McpWorkflowViewChangeNotifier> logger) : BackgroundService
{
    private readonly Channel<McpWorkflowViewOptions> _changes = Channel.CreateBounded<McpWorkflowViewOptions>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private IDisposable? _subscription;
    private string? _lastDefaultView;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _lastDefaultView = options.CurrentValue.DefaultView;
        _subscription = options.OnChange(changed => _changes.Writer.TryWrite(changed));
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var changed in _changes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await OnOptionsChangedAsync(changed).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                McpLog.WorkflowNotificationFailed(logger, exception);
            }
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }

    internal async ValueTask<int> OnOptionsChangedAsync(McpWorkflowViewOptions changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (string.Equals(changed.DefaultView, _lastDefaultView, StringComparison.Ordinal))
        {
            return 0;
        }

        var count = await publisher.BroadcastToolsListChanged().ConfigureAwait(false);
        _lastDefaultView = changed.DefaultView;
        return count;
    }
}
