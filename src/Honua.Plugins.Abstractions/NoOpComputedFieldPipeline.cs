// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// The zero-overhead <see cref="IComputedFieldPipeline"/> used when the plugin SDK is not licensed
/// or no computed-field providers are registered. It returns features unchanged — this is the
/// common production path, and a convenient default for unit tests that construct query handlers
/// directly.
/// </summary>
public sealed class NoOpComputedFieldPipeline : IComputedFieldPipeline
{
    /// <summary>A shared singleton instance.</summary>
    public static NoOpComputedFieldPipeline Instance { get; } = new();

    /// <inheritdoc />
    public bool HasComputedFields => false;

    /// <inheritdoc />
    public ValueTask<Feature> ProjectAsync(
        Feature feature,
        ComputedFieldContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(feature);
}
