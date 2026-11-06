// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

public sealed record GroundingRequest
{
    public required string Goal { get; init; }

    public WorkflowFamily? WorkflowFamilyHint { get; init; }

    public IntentConstraints? Constraints { get; init; }

    public IReadOnlyList<string> ExplicitInputs { get; init; } = [];

    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;

    public CallerContext Context { get; init; } = new();

    public ClarificationResponse? ClarificationResponse { get; init; }

    public string? IntentId { get; init; }
}

public sealed record CallerContext
{
    public string? WorkspaceId { get; init; }

    public string? PriorIntentId { get; init; }

    public string? PromotionScope { get; init; }
}
