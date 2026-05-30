// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Grounding.Spec;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SpecMutateRequestDto))]
[JsonSerializable(typeof(SpecGroundingContextDto))]
[JsonSerializable(typeof(SpecClarificationAnswerDto))]
[JsonSerializable(typeof(SpecMutateResponseDto))]
[JsonSerializable(typeof(SpecMutationPlanDto))]
[JsonSerializable(typeof(SpecMutationDto))]
[JsonSerializable(typeof(SpecClarificationDto))]
[JsonSerializable(typeof(SpecClarificationCandidateDto))]
[JsonSerializable(typeof(SpecDiagnosticDto))]
[JsonSerializable(typeof(SpecGroundingErrorDto))]
[JsonSerializable(typeof(SpecSummarizeRequestDto))]
[JsonSerializable(typeof(SpecSummarizeResponseDto))]
[JsonSerializable(typeof(SpecSectionSummaryDto))]
[JsonSerializable(typeof(List<SpecClarificationDto>))]
[JsonSerializable(typeof(List<SpecClarificationCandidateDto>))]
[JsonSerializable(typeof(List<SpecDiagnosticDto>))]
[JsonSerializable(typeof(List<SpecMutationDto>))]
[JsonSerializable(typeof(List<SpecSectionSummaryDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal sealed partial class SpecGroundingJsonContext : JsonSerializerContext;
