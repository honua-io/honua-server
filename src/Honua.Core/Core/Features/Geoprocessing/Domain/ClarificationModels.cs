// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record ClarificationRequest
{
    public required string IntentId { get; init; }

    public required IReadOnlyList<ClarificationReasonCode> ReasonCodes { get; init; }

    public required IReadOnlyList<ClarificationQuestion> Questions { get; init; }
}

public sealed record ClarificationQuestion
{
    public required string QuestionId { get; init; }

    public required ClarificationQuestionKind Kind { get; init; }

    public required string Prompt { get; init; }

    public IReadOnlyList<ClarificationOption>? Options { get; init; }
}

public sealed record ClarificationOption
{
    public required string Id { get; init; }

    public required string Label { get; init; }
}

public sealed record ClarificationResponse
{
    public required string IntentId { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Answers { get; init; }
}
