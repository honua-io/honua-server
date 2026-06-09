// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// The zero-overhead <see cref="IPluginEditPipeline"/> used when the plugin SDK is not licensed or
/// no plugins are registered. It allows every edit and performs no work — this is the common
/// production path. Also handy as a default for unit tests that construct protocol handlers directly.
/// </summary>
public sealed class NoOpPluginEditPipeline : IPluginEditPipeline
{
    /// <summary>A shared singleton instance.</summary>
    public static NoOpPluginEditPipeline Instance { get; } = new();

    /// <inheritdoc />
    public bool HasPlugins => false;

    /// <inheritdoc />
    public ValueTask<PluginEditOutcome> ValidateAndRunBeforeHooksAsync(
        EditHookContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(PluginEditOutcome.Allowed);

    /// <inheritdoc />
    public ValueTask RunAfterHooksAsync(EditHookContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
