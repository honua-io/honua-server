// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

public sealed record SpecCostEstimationResult
{
    public required SpecCostEstimate Estimate { get; init; }

    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}
