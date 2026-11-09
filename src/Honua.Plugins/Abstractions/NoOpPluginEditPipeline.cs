// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public sealed class NoOpPluginEditPipeline : IPluginEditPipeline
{
    public static NoOpPluginEditPipeline Instance { get; } = new();

    public bool HasPlugins => false;

    public ValueTask<PluginEditOutcome> ValidateAndRunBeforeHooksAsync(
        EditHookContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(PluginEditOutcome.Allowed);

    public ValueTask RunAfterHooksAsync(EditHookContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
