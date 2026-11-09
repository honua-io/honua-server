// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

public sealed record GroundingResult
{
    public required WorkflowFamilyClassification WorkflowFamily { get; init; }

    public required DraftIntent DraftIntent { get; init; }

    public required CandidateRanking Candidates { get; init; }

    public ClarificationRequest? Clarification { get; init; }

    public required string Engine { get; init; }
}
