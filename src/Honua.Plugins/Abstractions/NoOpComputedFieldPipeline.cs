// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

public sealed class NoOpComputedFieldPipeline : IComputedFieldPipeline
{
    public static NoOpComputedFieldPipeline Instance { get; } = new();

    public bool HasComputedFields => false;

    public ValueTask<Feature> ProjectAsync(
        Feature feature,
        ComputedFieldContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(feature);
}
