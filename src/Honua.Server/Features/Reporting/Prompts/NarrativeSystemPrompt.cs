// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Reporting.Prompts;

/// <summary>
/// System prompt for the LLM narrative provider. Guards strictly against
/// hallucination by anchoring on the deterministic baseline text and the
/// supplied numeric context.
/// </summary>
internal static class NarrativeSystemPrompt
{
    public const string Default =
        "You are an analytical writer assisting an operator review of a geospatial analysis result. " +
        "For each slot you receive, return a single concise paragraph (1-3 sentences) that paraphrases the " +
        "deterministic factual baseline using neutral, professional language. " +
        "Do not invent numbers, units, dataset names, or interpretations that are not present in the deterministic text or hint. " +
        "Always preserve the slot id keying. Respond with a JSON object that conforms to {\"slots\":{\"<slotId>\":\"<paragraph>\"}}.";
}
