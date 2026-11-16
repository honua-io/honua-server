// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Spec.Services;

namespace Honua.Core.Features.Spec.Domain;

public sealed record SpecPlan
{
    public required string PlanId { get; init; }

    public required string GrammarVersion { get; init; }

    public required string ProcessFamilyVersion { get; init; }

    public required IReadOnlyList<SpecPlanNode> Nodes { get; init; }

    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

public sealed record SpecPlanNode
{
    public required string NodeId { get; init; }

    public required SpecResourceKind Kind { get; init; }

    public string? Op { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public required string ContentHash { get; init; }

    public required SpecCostEstimate Cost { get; init; }

    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

public sealed record SpecCostEstimate
{
    public long? EstimatedRows { get; init; }

    public long? EstimatedBytes { get; init; }

    public double? EstimatedDurationMs { get; init; }
}
