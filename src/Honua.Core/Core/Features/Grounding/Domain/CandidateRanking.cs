// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

public sealed record GroundingCandidate
{
    public required string Id { get; init; }

    public required CandidateKind Kind { get; init; }

    public string? DisplayName { get; init; }

    public required double Score { get; init; }

    public required ConfidenceBand ConfidenceBand { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    // A feature-service candidate must not be drafted as a feature-layer publish source.
    public DatasetSubtype? DatasetSubtype { get; init; }
}

public sealed record CandidateRanking
{
    public IReadOnlyList<GroundingCandidate> Datasets { get; init; } = [];

    public IReadOnlyList<GroundingCandidate> Processes { get; init; } = [];

    public IReadOnlyList<GroundingCandidate> Templates { get; init; } = [];

    public IReadOnlyList<GroundingCandidate> Styles { get; init; } = [];

    public IReadOnlyList<GroundingCandidate> Deployments { get; init; } = [];
}
