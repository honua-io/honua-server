// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Grounding.Domain;

public sealed record MaterialAmbiguityFinding
{
    public required ClarificationReasonCode ReasonCode { get; init; }

    public required string QuestionId { get; init; }

    public required ClarificationQuestionKind QuestionKind { get; init; }

    public required string Prompt { get; init; }

    public IReadOnlyList<ClarificationOption>? Options { get; init; }
}
