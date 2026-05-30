// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Grounding.Spec;

internal sealed class SpecMutateRequestDto
{
    [JsonPropertyName("spec")]
    public JsonElement Spec { get; init; }

    [JsonPropertyName("turn")]
    public string? Turn { get; init; }

    [JsonPropertyName("context")]
    public SpecGroundingContextDto? Context { get; init; }

    [JsonPropertyName("clarification_answer")]
    public SpecClarificationAnswerDto? ClarificationAnswer { get; init; }
}

internal sealed class SpecGroundingContextDto
{
    [JsonPropertyName("target_id")]
    public string? TargetId { get; init; }

    [JsonPropertyName("default_crs")]
    public string? DefaultCrs { get; init; }

    [JsonPropertyName("default_unit")]
    public string? DefaultUnit { get; init; }

    [JsonPropertyName("hints")]
    public List<string>? Hints { get; init; }
}

internal sealed class SpecClarificationAnswerDto
{
    [JsonPropertyName("intent_id")]
    public string? IntentId { get; init; }

    [JsonPropertyName("answers")]
    public Dictionary<string, List<string>>? Answers { get; init; }
}

internal sealed class SpecMutateResponseDto
{
    [JsonPropertyName("mutation")]
    public SpecMutationPlanDto? Mutation { get; init; }

    [JsonPropertyName("clarifications")]
    public List<SpecClarificationDto> Clarifications { get; init; } = [];

    [JsonPropertyName("warnings")]
    public List<SpecDiagnosticDto> Warnings { get; init; } = [];

    [JsonPropertyName("error")]
    public SpecGroundingErrorDto? Error { get; init; }
}

internal sealed class SpecMutationPlanDto
{
    [JsonPropertyName("mutations")]
    public List<SpecMutationDto> Mutations { get; init; } = [];

    [JsonPropertyName("next_spec")]
    public JsonElement NextSpec { get; init; }

    [JsonPropertyName("sections_touched")]
    public List<string> SectionsTouched { get; init; } = [];

    [JsonPropertyName("sections_preserved")]
    public List<string> SectionsPreserved { get; init; } = [];
}

internal sealed class SpecMutationDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("source_id")]
    public string? SourceId { get; init; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("source_ref")]
    public string? SourceRef { get; init; }

    [JsonPropertyName("target_id")]
    public string? TargetId { get; init; }

    [JsonPropertyName("predicate")]
    public string? Predicate { get; init; }

    [JsonPropertyName("compute_id")]
    public string? ComputeId { get; init; }

    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonPropertyName("inputs")]
    public Dictionary<string, string>? Inputs { get; init; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, string>? Parameters { get; init; }

    [JsonPropertyName("layer_ids")]
    public List<string>? LayerIds { get; init; }

    [JsonPropertyName("viewport")]
    public Dictionary<string, string>? Viewport { get; init; }

    [JsonPropertyName("output_id")]
    public string? OutputId { get; init; }

    [JsonPropertyName("expression")]
    public string? Expression { get; init; }

    [JsonPropertyName("from_id")]
    public string? FromId { get; init; }

    [JsonPropertyName("to_id")]
    public string? ToId { get; init; }
}

internal sealed class SpecClarificationDto
{
    [JsonPropertyName("intent_id")]
    public string IntentId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("reason_codes")]
    public List<string> ReasonCodes { get; init; } = [];

    [JsonPropertyName("question_id")]
    public string QuestionId { get; init; } = string.Empty;

    [JsonPropertyName("question_kind")]
    public string QuestionKind { get; init; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = string.Empty;

    [JsonPropertyName("candidates")]
    public List<SpecClarificationCandidateDto>? Candidates { get; init; }
}

internal sealed class SpecClarificationCandidateDto
{
    [JsonPropertyName("candidate_type")]
    public string CandidateType { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("catalog_ref")]
    public string? CatalogRef { get; init; }

    [JsonPropertyName("schema_preview")]
    public List<string>? SchemaPreview { get; init; }

    [JsonPropertyName("column_name")]
    public string? ColumnName { get; init; }

    [JsonPropertyName("type_ref")]
    public string? TypeRef { get; init; }

    [JsonPropertyName("nullable")]
    public bool? Nullable { get; init; }

    [JsonPropertyName("sample")]
    public string? Sample { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("count")]
    public long? Count { get; init; }

    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }

    [JsonPropertyName("operator_name")]
    public string? OperatorName { get; init; }
}

internal sealed class SpecDiagnosticDto
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

internal sealed class SpecGroundingErrorDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

internal sealed class SpecSummarizeRequestDto
{
    [JsonPropertyName("spec")]
    public JsonElement Spec { get; init; }
}

internal sealed class SpecSummarizeResponseDto
{
    [JsonPropertyName("title_summary")]
    public string TitleSummary { get; init; } = string.Empty;

    [JsonPropertyName("section_summaries")]
    public List<SpecSectionSummaryDto> SectionSummaries { get; init; } = [];
}

internal sealed class SpecSectionSummaryDto
{
    [JsonPropertyName("section_id")]
    public string SectionId { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
}
