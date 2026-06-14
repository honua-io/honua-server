// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Host-facing orchestrator that runs the registered plugin <see cref="IFeatureValidator"/>s and
/// <see cref="IEditHook"/>s at the appropriate points of a protocol edit pipeline. A single
/// implementation is resolved from DI; when no Enterprise license / no plugins are present the
/// host registers <see cref="NoOpPluginEditPipeline"/> so the common path has zero overhead.
/// </summary>
/// <remarks>
/// This is the only plugin type a protocol assembly needs to consume — it depends solely on this
/// abstraction package and never on plugin internals.
/// </remarks>
public interface IPluginEditPipeline
{
    /// <summary>
    /// Gets whether any plugins are active. Hosts can skip building an
    /// <see cref="EditHookContext"/> entirely when this is <see langword="false"/>.
    /// </summary>
    bool HasPlugins { get; }

    /// <summary>
    /// Runs per-feature validators followed by before-edit hooks over the batch.
    /// </summary>
    /// <param name="context">The batch of features about to be written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregate rejection outcome; <see cref="PluginEditOutcome.Allowed"/> when nothing was rejected.</returns>
    ValueTask<PluginEditOutcome> ValidateAndRunBeforeHooksAsync(
        EditHookContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs after-edit hooks following a successful write. Best-effort: failures are logged and
    /// swallowed so they never roll back an already-committed edit.
    /// </summary>
    /// <param name="context">The batch of features that were written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RunAfterHooksAsync(EditHookContext context, CancellationToken cancellationToken);
}
