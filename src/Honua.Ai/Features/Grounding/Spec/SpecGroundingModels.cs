// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Ai.Grounding.Spec;

internal enum SpecGroundingErrorKind
{
    Unresolvable,
    Ambiguous,
    InvalidMutation,
    OutOfScope
}

internal enum SpecMutationKind
{
    AddSource,
    RemoveSource,
    AddScopeClause,
    AddCompute,
    RemoveCompute,
    SetMapLayer,
    SetViewport,
    SetOutput,
    RenameReference
}

internal abstract record SpecMutation(SpecMutationKind Kind);

internal sealed record AddSourceMutation(
    string SourceId,
    string SourceType,
    string SourceRef,
    string? SourceTitle = null) : SpecMutation(SpecMutationKind.AddSource);

internal sealed record RemoveSourceMutation(string SourceId)
    : SpecMutation(SpecMutationKind.RemoveSource);

internal sealed record AddScopeClauseMutation(
    string TargetId,
    string Predicate)
    : SpecMutation(SpecMutationKind.AddScopeClause);

internal sealed record AddComputeMutation(
    string ComputeId,
    string OperatorName,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string>? Parameters = null)
    : SpecMutation(SpecMutationKind.AddCompute);

internal sealed record RemoveComputeMutation(string ComputeId)
    : SpecMutation(SpecMutationKind.RemoveCompute);

internal sealed record SetMapLayerMutation(IReadOnlyList<string> LayerIds)
    : SpecMutation(SpecMutationKind.SetMapLayer);

internal sealed record SetViewportMutation(IReadOnlyDictionary<string, string> Values)
    : SpecMutation(SpecMutationKind.SetViewport);

internal sealed record SetOutputMutation(string OutputId, string Expression)
    : SpecMutation(SpecMutationKind.SetOutput);

internal sealed record RenameReferenceMutation(string FromId, string ToId)
    : SpecMutation(SpecMutationKind.RenameReference);

internal sealed record SpecGroundingContext(
    string? TargetId = null,
    string? DefaultCrs = null,
    string? DefaultUnit = null,
    IReadOnlyList<string>? Hints = null);

internal sealed record SpecClarificationCandidate
{
    public required string CandidateType { get; init; }

    public required string Id { get; init; }

    public required string Label { get; init; }

    public string? CatalogRef { get; init; }

    public IReadOnlyList<string>? SchemaPreview { get; init; }

    public string? ColumnName { get; init; }

    public string? TypeRef { get; init; }

    public bool? Nullable { get; init; }

    public string? Sample { get; init; }

    public string? Value { get; init; }

    public long? Count { get; init; }

    public string? Unit { get; init; }

    public string? Crs { get; init; }

    public string? OperatorName { get; init; }
}

internal sealed record SpecClarificationEnvelope(
    ClarificationRequest Request,
    IReadOnlyDictionary<string, IReadOnlyList<SpecClarificationCandidate>> CandidatesByQuestionId);

internal sealed record SpecMutationPlan(
    IReadOnlyList<SpecMutation> Mutations,
    SpecDocument NextSpec,
    string NextSpecCanonicalJson,
    IReadOnlyList<string> SectionsTouched,
    IReadOnlyList<string> SectionsPreserved);

internal sealed record SpecGroundingResult
{
    public SpecMutationPlan? Mutation { get; init; }

    public SpecClarificationEnvelope? Clarification { get; init; }

    public SpecGroundingErrorKind? ErrorKind { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<SpecDiagnostic> Warnings { get; init; } = [];
}

internal sealed record SpecSectionSummary(string SectionId, string Text);

internal sealed record SpecSummary(string TitleSummary, IReadOnlyList<SpecSectionSummary> Sections);
