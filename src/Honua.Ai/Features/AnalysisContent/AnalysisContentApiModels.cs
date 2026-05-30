// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Ai.AnalysisContent;

internal sealed record CreateAnalysisContentItemRequest
{
    public AnalysisContentKind Kind { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Title { get; init; }

    public SavedQueryContent? SavedQuery { get; init; }

    public AnalysisPackageContent? AnalysisPackage { get; init; }
}

internal sealed record CreateAnalysisContentVersionRequest
{
    public SavedQueryContent? SavedQuery { get; init; }

    public AnalysisPackageContent? AnalysisPackage { get; init; }

    public string? BasedOnVersionId { get; init; }

    public string? CreatedFromJobId { get; init; }

    public IReadOnlyList<string>? CreatedFromArtifactIds { get; init; }
}

internal sealed record PreviewSavedQueryRequest
{
    public int? Limit { get; init; }
}

internal sealed record RunAnalysisContentVersionRequest
{
    public string? IdempotencyKey { get; init; }

    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

internal sealed record RerunAnalysisContentVersionRequest
{
    public string? IdempotencyKey { get; init; }

    public string? RerunOfJobId { get; init; }

    public string? RerunOfResultPackageId { get; init; }

    public IReadOnlyDictionary<string, string>? ParameterOverrides { get; init; }
}

internal sealed record AnalysisContentItemResponse
{
    public required AnalysisContentItem Item { get; init; }

    public required AnalysisContentVersion Version { get; init; }
}

internal sealed record AnalysisContentVersionResponse
{
    public required AnalysisContentItem Item { get; init; }

    public required AnalysisContentVersion Version { get; init; }
}

internal sealed record AnalysisContentJobResponse
{
    public required string JobId { get; init; }

    public required ExecutionJobStatus Status { get; init; }

    public required AnalysisContentVersion Version { get; init; }
}

internal sealed record AnalysisArtifactResponse
{
    public required ResultArtifactRecord Artifact { get; init; }

    public required ArtifactBindingRef Binding { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CreateAnalysisContentItemRequest))]
[JsonSerializable(typeof(CreateAnalysisContentVersionRequest))]
[JsonSerializable(typeof(PreviewSavedQueryRequest))]
[JsonSerializable(typeof(RunAnalysisContentVersionRequest))]
[JsonSerializable(typeof(RerunAnalysisContentVersionRequest))]
[JsonSerializable(typeof(AnalysisContentItemResponse))]
[JsonSerializable(typeof(AnalysisContentVersionResponse))]
[JsonSerializable(typeof(AnalysisContentJobResponse))]
[JsonSerializable(typeof(AnalysisArtifactResponse))]
[JsonSerializable(typeof(SavedQueryPreviewResult))]
[JsonSerializable(typeof(AnalysisJobLogs))]
[JsonSerializable(typeof(AnalysisJobFailure))]
[JsonSerializable(typeof(ProblemDetailsResponse))]
[JsonSerializable(typeof(AnalysisContentItem))]
[JsonSerializable(typeof(AnalysisContentVersion))]
[JsonSerializable(typeof(SavedQueryContent))]
[JsonSerializable(typeof(AnalysisPackageContent))]
[JsonSerializable(typeof(ResultArtifactRecord))]
[JsonSerializable(typeof(ArtifactBindingRef))]
[JsonSerializable(typeof(AnalysisPlan))]
[JsonSerializable(typeof(AnalysisPlanStep))]
[JsonSerializable(typeof(AnalysisIntent))]
internal sealed partial class AnalysisContentApiJsonContext : JsonSerializerContext
{
}
