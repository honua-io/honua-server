// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Core.Features.AnalysisContent;

/// <summary>
/// Source-generated JSON metadata for analysis content domain contracts.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AnalysisContentItem))]
[JsonSerializable(typeof(AnalysisContentVersion))]
[JsonSerializable(typeof(AnalysisContentVersion[]))]
[JsonSerializable(typeof(SavedQueryContent))]
[JsonSerializable(typeof(AnalysisPackageContent))]
[JsonSerializable(typeof(ArtifactBindingRef))]
[JsonSerializable(typeof(ArtifactBindingRef[]))]
[JsonSerializable(typeof(ResultArtifactRecord))]
[JsonSerializable(typeof(ResultArtifactRecord[]))]
[JsonSerializable(typeof(SavedQueryPreviewResult))]
[JsonSerializable(typeof(SavedQueryPreviewFeature))]
[JsonSerializable(typeof(SavedQueryPreviewFeature[]))]
[JsonSerializable(typeof(AnalysisJobFailure))]
[JsonSerializable(typeof(AnalysisJobLogs))]
[JsonSerializable(typeof(AnalysisJobLogEntry))]
[JsonSerializable(typeof(AnalysisJobLogEntry[]))]
[JsonSerializable(typeof(ExecutionLogEntry))]
[JsonSerializable(typeof(FilterPlan))]
[JsonSerializable(typeof(FilterPlanClause))]
[JsonSerializable(typeof(FilterPlanClause[]))]
[JsonSerializable(typeof(ComparisonClause))]
[JsonSerializable(typeof(SpatialClause))]
[JsonSerializable(typeof(TemporalClause))]
[JsonSerializable(typeof(NestedClause))]
[JsonSerializable(typeof(AnalysisIntent))]
[JsonSerializable(typeof(IntentConstraints))]
[JsonSerializable(typeof(AnalysisPlan))]
[JsonSerializable(typeof(AnalysisPlanStep))]
[JsonSerializable(typeof(AnalysisPlanStep[]))]
[JsonSerializable(typeof(AnalysisResultPackage))]
[JsonSerializable(typeof(ArtifactRef))]
[JsonSerializable(typeof(ArtifactRef[]))]
[JsonSerializable(typeof(WorkspaceRef))]
[JsonSerializable(typeof(WorkspaceRef[]))]
[JsonSerializable(typeof(ProvenanceRecord))]
[JsonSerializable(typeof(ProvenanceSource))]
[JsonSerializable(typeof(ProvenanceSource[]))]
[JsonSerializable(typeof(ResultSummary))]
[JsonSerializable(typeof(GeoprocessingError))]
[JsonSerializable(typeof(GeoprocessingError[]))]
[JsonSerializable(typeof(GeoprocessingValidationFailure))]
[JsonSerializable(typeof(GeoprocessingValidationFailure[]))]
[JsonSerializable(typeof(ImmutableDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
public sealed partial class AnalysisContentJsonContext : JsonSerializerContext
{
}
