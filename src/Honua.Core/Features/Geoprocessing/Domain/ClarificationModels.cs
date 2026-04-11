// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// A request for clarification from the user before a workflow can proceed.
/// </summary>
public sealed record ClarificationRequest
{
    /// <summary>
    /// Identifier of the intent that triggered this clarification.
    /// </summary>
    public required string IntentId { get; init; }

    /// <summary>
    /// Reason codes explaining why clarification is needed.
    /// </summary>
    public required IReadOnlyList<ClarificationReasonCode> ReasonCodes { get; init; }

    /// <summary>
    /// Questions to present to the user.
    /// </summary>
    public required IReadOnlyList<ClarificationQuestion> Questions { get; init; }
}

/// <summary>
/// A single question within a clarification request.
/// </summary>
public sealed record ClarificationQuestion
{
    /// <summary>
    /// Unique identifier for this question.
    /// </summary>
    public required string QuestionId { get; init; }

    /// <summary>
    /// Interaction type for this question.
    /// </summary>
    public required ClarificationQuestionKind Kind { get; init; }

    /// <summary>
    /// Human-readable prompt text for the question.
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Available options for single-select and multi-select questions.
    /// </summary>
    public IReadOnlyList<ClarificationOption>? Options { get; init; }
}

/// <summary>
/// A selectable option within a clarification question.
/// </summary>
public sealed record ClarificationOption
{
    /// <summary>
    /// Unique identifier for this option.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable label for this option.
    /// </summary>
    public required string Label { get; init; }
}

/// <summary>
/// User-provided answers to a clarification request.
/// </summary>
public sealed record ClarificationResponse
{
    /// <summary>
    /// Identifier of the intent being clarified.
    /// </summary>
    public required string IntentId { get; init; }

    /// <summary>
    /// Answers keyed by question identifier. Each answer is a list of values to support
    /// multi-select questions. Single-select, free-text, and confirmation answers use a
    /// single-element list.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Answers { get; init; }
}
