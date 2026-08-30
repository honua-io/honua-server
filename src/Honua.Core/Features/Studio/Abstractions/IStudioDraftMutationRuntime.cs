// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

/// <summary>Canonical durable operation boundary for Studio draft mutations.</summary>
public interface IStudioDraftMutationRuntime
{
    Task<StudioDraftMutationReceipt<StudioPackageDraft>> CreateAsync(
        CreateStudioPackageDraftCommand command,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioPackageDraft>> UpdateAsync(
        Guid draftId,
        UpdateStudioPackageDraftCommand command,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<bool>> DeleteAsync(
        Guid draftId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioValidationSummary>> ValidateAsync(
        Guid draftId,
        string? actorId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioPreviewPlan>> PreviewAsync(
        Guid draftId,
        string? actorId,
        StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);

    Task<StudioDraftMutationReceipt<StudioContentVersion>> SaveVersionAsync(
        Guid draftId, long expectedGeneration, string? changeNote, string? actorId, StudioDraftMutationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Trusted evidence supplied by an authorized protocol adapter.</summary>
public sealed record StudioDraftMutationContext
{
    public string? PrincipalId { get; init; }
    public string? TenantId { get; init; }
    public string? SchemaName { get; init; }
    public string? CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string AuthorizationOutcome { get; init; } = "authorized";
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>Durable envelope plus the typed projection produced by its actuator.</summary>
public sealed record StudioDraftMutationReceipt<T>
{
    public required OperationHandle Operation { get; init; }
    public T? Value { get; init; }
}
