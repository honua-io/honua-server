// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Server.Features.Spec.Models;

internal sealed class SpecDocumentRequest
{
    public string GrammarVersion { get; init; } = string.Empty;

    public string ProcessFamilyVersion { get; init; } = string.Empty;

    public string? SpecId { get; init; }

    public IReadOnlyList<SpecNodeRequest> Nodes { get; init; } = [];

    public SpecCacheMode? CacheMode { get; init; }

    public int? MaxConcurrency { get; init; }
}

internal sealed class SpecValidateRequest
{
    public string? Text { get; init; }

    public JsonElement Spec { get; init; }

    public bool IncludeCanonicalJson { get; init; }
}

internal sealed class SpecNodeRequest
{
    public string Id { get; init; } = string.Empty;

    // A missing resource kind is rejected rather than defaulted.
    public SpecResourceKind? Kind { get; init; }

    public string? Op { get; init; }

    public IReadOnlyDictionary<string, string>? Inputs { get; init; }

    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    public string? CanonicalFragment { get; init; }

    public IReadOnlyDictionary<string, string>? SourcePins { get; init; }

    public bool Nondeterministic { get; init; }
}

internal sealed class SpecPlanResponse
{
    public required string PlanId { get; init; }

    public required string GrammarVersion { get; init; }

    public required string ProcessFamilyVersion { get; init; }

    public required IReadOnlyList<SpecPlanNodeResponse> Nodes { get; init; }

    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

internal sealed class SpecValidateResponse
{
    public required bool IsValid { get; init; }

    public required IReadOnlyList<SpecValidateDiagnosticDto> Diagnostics { get; init; }

    public string? CanonicalJson { get; init; }

    public string? Grammar { get; init; }

    public required string OperatorCapabilityVersion { get; init; }
}

internal sealed class SpecValidateDiagnosticDto
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Message { get; init; }

    public string? Path { get; init; }

    public SpecSourceSpanDto? Span { get; init; }
}

internal sealed class SpecSourceSpanDto
{
    public required int Line { get; init; }

    public required int Column { get; init; }

    public required int Offset { get; init; }

    public required int Length { get; init; }
}

internal sealed class SpecPlanNodeResponse
{
    public required string NodeId { get; init; }

    public required SpecResourceKind Kind { get; init; }

    public string? Op { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public required string ContentHash { get; init; }

    public required SpecCostEstimate Cost { get; init; }

    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

internal sealed class SpecCancelRequest
{
    public string ApplyToken { get; init; } = string.Empty;
}

internal sealed class SpecCancelResponse
{
    public required string ApplyToken { get; init; }

    public required bool Cancelled { get; init; }
}

internal sealed class SpecProblem
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public required string Detail { get; init; }

    public required string Code { get; init; }

    public string? NodeId { get; init; }

    public string? Remedy { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SpecDocumentRequest))]
[JsonSerializable(typeof(SpecNodeRequest))]
[JsonSerializable(typeof(SpecPlanResponse))]
[JsonSerializable(typeof(SpecPlanNodeResponse))]
[JsonSerializable(typeof(SpecValidateRequest))]
[JsonSerializable(typeof(SpecValidateResponse))]
[JsonSerializable(typeof(SpecValidateDiagnosticDto))]
[JsonSerializable(typeof(SpecSourceSpanDto))]
[JsonSerializable(typeof(SpecCancelRequest))]
[JsonSerializable(typeof(SpecCancelResponse))]
[JsonSerializable(typeof(SpecProblem))]
[JsonSerializable(typeof(SpecApplyEvent))]
[JsonSerializable(typeof(SpecWarning))]
[JsonSerializable(typeof(SpecCostEstimate))]
[JsonSerializable(typeof(SpecCostActual))]
[JsonSerializable(typeof(SpecApplySummary))]
internal sealed partial class SpecJsonContext : JsonSerializerContext
{
}
