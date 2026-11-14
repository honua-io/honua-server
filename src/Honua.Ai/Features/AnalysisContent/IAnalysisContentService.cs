// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Ai.AnalysisContent;

internal sealed record AnalysisContentItemResult(
    AnalysisContentItem Item,
    AnalysisContentVersion Version);

internal sealed record ListAnalysisContentItemsQuery(
    AnalysisContentKind? Kind,
    AnalysisContentLifecycle? Lifecycle,
    int? Limit,
    int? Offset);

internal sealed record AnalysisContentListResult(
    IReadOnlyList<AnalysisContentItem> Items,
    long TotalCount,
    int Limit,
    int Offset);

internal sealed record AnalysisContentEstimateResult(
    string ItemId,
    int Version,
    string VersionId,
    AnalysisContentEstimate Estimate);

internal sealed record AnalysisContentVersionResult(
    AnalysisContentItem Item,
    AnalysisContentVersion Version);

internal sealed record AnalysisContentJobResult(
    ExecutionJobRecord Job,
    AnalysisContentVersion Version);

internal sealed record CreateAnalysisContentItemCommand(
    AnalysisContentKind Kind,
    string Name,
    string? Title,
    SavedQueryContent? SavedQuery,
    AnalysisPackageContent? AnalysisPackage);

internal sealed record CreateAnalysisContentVersionCommand(
    SavedQueryContent? SavedQuery,
    AnalysisPackageContent? AnalysisPackage,
    string? BasedOnVersionId,
    string? CreatedFromJobId,
    IReadOnlyList<string>? CreatedFromArtifactIds);

internal sealed record RunAnalysisContentVersionCommand(
    string? IdempotencyKey,
    IReadOnlyDictionary<string, string>? Parameters);

internal sealed record RerunAnalysisContentVersionCommand(
    string? IdempotencyKey,
    string? RerunOfJobId,
    string? RerunOfResultPackageId,
    IReadOnlyDictionary<string, string>? ParameterOverrides);
