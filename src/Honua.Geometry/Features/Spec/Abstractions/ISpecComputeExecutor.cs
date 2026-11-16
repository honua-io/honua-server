// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

public interface ISpecComputeExecutor
{
    Task<SpecComputeResult> ExecuteAsync(
        CanonicalSpecNode node,
        string contentHash,
        IReadOnlyDictionary<string, CachedArtifactRef> inputs,
        CancellationToken cancellationToken);
}

public sealed record SpecComputeResult
{
    public required SpecArtifactPayload Payload { get; init; }

    public required SpecCostActual ActualCost { get; init; }

    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}
