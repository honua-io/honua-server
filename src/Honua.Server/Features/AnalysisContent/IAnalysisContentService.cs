// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.AnalysisContent;

internal interface IAnalysisContentService
{
    Task<AnalysisContentItemResult> CreateItemAsync(
        CreateAnalysisContentItemCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<AnalysisContentItemResult> GetItemAsync(
        string itemId,
        CancellationToken cancellationToken);

    Task<AnalysisContentVersionResult> GetVersionAsync(
        string itemId,
        int? version,
        CancellationToken cancellationToken);

    Task<AnalysisContentVersionResult> AddVersionAsync(
        string itemId,
        CreateAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<SavedQueryPreviewResult> PreviewSavedQueryAsync(
        string itemId,
        int version,
        int? limit,
        CancellationToken cancellationToken);

    Task<AnalysisContentJobResult> SubmitAnalysisPackageAsync(
        string itemId,
        int version,
        RunAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<AnalysisContentJobResult> RerunAnalysisPackageAsync(
        string itemId,
        int version,
        RerunAnalysisContentVersionCommand command,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<ResultArtifactRecord> GetArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken);

    Task<AnalysisJobLogs> GetJobLogsAsync(
        string jobId,
        int? limit,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<AnalysisJobFailure> GetJobFailureAsync(
        string jobId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

internal sealed record AnalysisContentItemResult(
    AnalysisContentItem Item,
    AnalysisContentVersion Version);

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
