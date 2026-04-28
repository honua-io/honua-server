// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Server.Features.Spec.Models;

/// <summary>
/// Body of <c>POST /v1/spec/plan</c> and <c>POST /v1/spec/apply</c>.
/// </summary>
internal sealed class SpecDocumentRequest
{
    /// <summary>Declared grammar version.</summary>
    public string GrammarVersion { get; init; } = string.Empty;

    /// <summary>Declared process-family version.</summary>
    public string ProcessFamilyVersion { get; init; } = string.Empty;

    /// <summary>Optional stable document identifier.</summary>
    public string? SpecId { get; init; }

    /// <summary>Declared spec nodes.</summary>
    public IReadOnlyList<SpecNodeRequest> Nodes { get; init; } = [];

    /// <summary>Apply-only: cache mode. Ignored by <c>/plan</c>.</summary>
    public SpecCacheMode? CacheMode { get; init; }

    /// <summary>Apply-only: maximum node concurrency. Ignored by <c>/plan</c>.</summary>
    public int? MaxConcurrency { get; init; }
}

/// <summary>
/// Body of <c>POST /v1/spec/validate</c>.
/// </summary>
internal sealed class SpecValidateRequest
{
    /// <summary>Spec source text in the public DSL.</summary>
    public string? Text { get; init; }

    /// <summary>Canonical JSON spec document emitted by <c>SpecCanonicalizer</c>.</summary>
    public JsonElement Spec { get; init; }

    /// <summary>When true, valid requests include canonical JSON in the response.</summary>
    public bool IncludeCanonicalJson { get; init; }
}

/// <summary>
/// Single node in a <see cref="SpecDocumentRequest"/>.
/// </summary>
internal sealed class SpecNodeRequest
{
    /// <summary>Stable node identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Canonical resource kind. Required — a missing or omitted field is
    /// rejected at the transport boundary with the <c>unknown-kind</c>
    /// diagnostic rather than silently defaulting to <see cref="SpecResourceKind.Compute"/>.
    /// </summary>
    public SpecResourceKind? Kind { get; init; }

    /// <summary>Operator identifier (nullable for sources).</summary>
    public string? Op { get; init; }

    /// <summary>Input bindings — scalar literals or <c>@node-id</c> references.</summary>
    public IReadOnlyDictionary<string, string>? Inputs { get; init; }

    /// <summary>Flat parameter bag.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>Canonicalised fragment (optional).</summary>
    public string? CanonicalFragment { get; init; }

    /// <summary>Optional declared source pins.</summary>
    public IReadOnlyDictionary<string, string>? SourcePins { get; init; }

    /// <summary>Whether the op is declared non-deterministic.</summary>
    public bool Nondeterministic { get; init; }
}

/// <summary>
/// Response body for <c>POST /v1/spec/plan</c>.
/// </summary>
internal sealed class SpecPlanResponse
{
    public required string PlanId { get; init; }

    public required string GrammarVersion { get; init; }

    public required string ProcessFamilyVersion { get; init; }

    public required IReadOnlyList<SpecPlanNodeResponse> Nodes { get; init; }

    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

/// <summary>
/// Response body for <c>POST /v1/spec/validate</c>.
/// </summary>
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

/// <summary>
/// Single plan-node representation in a <see cref="SpecPlanResponse"/>.
/// </summary>
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

/// <summary>
/// Request body for <c>POST /v1/spec/cancel</c>.
/// </summary>
internal sealed class SpecCancelRequest
{
    /// <summary>Apply token to cancel.</summary>
    public string ApplyToken { get; init; } = string.Empty;
}

/// <summary>
/// Response body for <c>POST /v1/spec/cancel</c>.
/// </summary>
internal sealed class SpecCancelResponse
{
    public required string ApplyToken { get; init; }

    public required bool Cancelled { get; init; }
}

/// <summary>
/// Minimal problem-details envelope used by spec endpoints for structured
/// errors. Keyed on the stable diagnostic <c>code</c>.
/// </summary>
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

/// <summary>
/// Source-generated JSON context for spec request/response models. Keeps the
/// feature AOT-safe without the reflection-based serializer.
/// </summary>
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
