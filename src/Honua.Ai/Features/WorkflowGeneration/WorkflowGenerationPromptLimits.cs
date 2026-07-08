// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.WorkflowGeneration;

/// <summary>
/// Shared inbound prompt-size bound for the model-calling generation services
/// (analysis, dashboard, form, map, query, report). Provider <c>MaxTokens</c> caps the model's
/// output, not the user-supplied prompt, so an oversized prompt would still be shipped and could
/// exhaust the provider's token budget or trigger a billing spike. Capping here mirrors the
/// NlQuery <c>MaxQueryCharacters</c> guard so every model-calling path shares the same
/// abuse/cost protection.
/// </summary>
internal static class WorkflowGenerationPromptLimits
{
    /// <summary>
    /// Maximum allowed prompt length in characters. 8,000 is well below every current provider
    /// context window and far larger than any reasonable generation prompt.
    /// </summary>
    public const int MaxPromptCharacters = 8_000;

    /// <summary>Human-readable rationale returned when a prompt exceeds <see cref="MaxPromptCharacters"/>.</summary>
    public const string ExceededMessage =
        "The prompt exceeds the 8,000-character limit. Please shorten the request and try again.";
}
