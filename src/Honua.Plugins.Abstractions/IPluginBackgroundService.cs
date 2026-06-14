// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A plugin extension point for a long-running background task. The host hosts each registered
/// provider as an <c>IHostedService</c> behind a failure-isolation wrapper so a plugin that throws
/// during start or execution is logged and disabled without crashing the server process or
/// affecting other plugins.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are resolved as singletons. <see cref="ExecuteAsync"/> is invoked once at
/// startup with a token that is cancelled at shutdown; it should loop until the token trips and
/// must observe cancellation promptly. The whole feature is Enterprise-gated (<c>plugin.sdk</c>)
/// and respects the operator kill-switch — an unlicensed/disabled host never starts the service.
/// </para>
/// <para>
/// Background services require the <see cref="PluginCapability.BackgroundExecution"/> capability
/// to be declared on the plugin's <c>[Plugin]</c> manifest; the host refuses to host a background
/// service that has not declared it.
/// </para>
/// </remarks>
public interface IPluginBackgroundService
{
    /// <summary>
    /// Runs the background work. Returns when the work completes or when
    /// <paramref name="stoppingToken"/> is cancelled at shutdown.
    /// </summary>
    /// <param name="stoppingToken">Triggered when the host is shutting down.</param>
    Task ExecuteAsync(CancellationToken stoppingToken);
}
