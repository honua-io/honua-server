// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Views;

internal sealed class McpWorkflowViewChangeNotifier : IHostedService, IDisposable
{
    private readonly IOptionsMonitor<McpWorkflowViewOptions> _options;
    private readonly IMcpNotificationPublisher _publisher;
    private IDisposable? _subscription;
    private string? _lastDefaultView;

    public McpWorkflowViewChangeNotifier(
        IOptionsMonitor<McpWorkflowViewOptions> options,
        IMcpNotificationPublisher publisher)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lastDefaultView = _options.CurrentValue.DefaultView;
        _subscription = _options.OnChange(changed => OnOptionsChanged(changed));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    internal int OnOptionsChanged(McpWorkflowViewOptions changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        if (string.Equals(changed.DefaultView, _lastDefaultView, StringComparison.Ordinal))
        {
            return 0;
        }

        _lastDefaultView = changed.DefaultView;
        return _publisher.BroadcastToolsListChanged();
    }
}
