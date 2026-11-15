// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Combined output of cost estimation: the estimate and any warnings surfaced
/// while computing it.
/// </summary>
public sealed record SpecCostEstimationResult
{
    /// <summary>Cost estimate; unknown fields are null.</summary>
    public required SpecCostEstimate Estimate { get; init; }

    /// <summary>Per-node diagnostics emitted during estimation.</summary>
    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}
