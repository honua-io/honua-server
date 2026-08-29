// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// Keeps view membership fresh for live sessions (honua-server#3428). When the
/// server-profile view configuration changes, every active session is told to
/// re-read its tool list by broadcasting <c>notifications/tools/list_changed</c>
/// — the same notification the catalog-mutating publish path emits, so an HTTP
/// client and an SDK stdio proxy observe an identical refresh signal.
/// </summary>
/// <remarks>
/// View <em>revision</em> changes ship with the binary (a view definition is
/// server-authored code, digest-pinned by <see cref="McpWorkflowViewProjection.RevisionDigest"/>),
/// and identity changes already re-mint a session. The remaining runtime axis is
/// the profile configuration, which this notifier watches.
/// </remarks>
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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lastDefaultView = _options.CurrentValue.DefaultView;
        _subscription = _options.OnChange(changed => OnOptionsChanged(changed));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _subscription?.Dispose();

    /// <summary>
    /// Broadcasts <c>tools/list_changed</c> when the effective profile default
    /// view actually changed. Exposed for tests so the refresh contract can be
    /// asserted without a configuration-reload harness.
    /// </summary>
    /// <returns>The number of sessions notified; <c>0</c> when nothing changed.</returns>
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
