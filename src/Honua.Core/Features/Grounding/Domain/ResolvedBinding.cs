// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

public sealed record ResolvedBinding
{
    public required string QuestionId { get; init; }

    public required CandidateKind Kind { get; init; }

    public required string CandidateId { get; init; }

    public string? DisplayName { get; init; }

    public required double Score { get; init; }

    public required double RunnerUpMargin { get; init; }
}
