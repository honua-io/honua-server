// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

public interface IStudioPackageStore
{
    StudioPackagePersistenceMode PersistenceMode { get; }

    Task<StudioPackageDraft> CreateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default);

    Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    Task<StudioDraftRevision?> GetDraftRevisionAsync(Guid draftId, long generation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudioDraftRevisionSummary>> ListDraftRevisionsAsync(StudioDraftRevisionQuery query, CancellationToken cancellationToken = default);

    Task<StudioPackageDraft?> UpdateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default);

    Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    Task<bool> DeleteDraftAsync(Guid draftId, long expectedGeneration, CancellationToken cancellationToken = default);

    Task<StudioContentVersion> CreateVersionAsync(StudioPackageDraft draft, string? changeNote, string? actorId, CancellationToken cancellationToken = default);

    Task<StudioContentVersion> CreateCheckpointVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        StudioVersionCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<StudioCheckpointedVersion?> GetLatestCheckpointVersionAsync(
        string mapId,
        long afterCursor,
        long throughCursor,
        CancellationToken cancellationToken = default);

    Task AcknowledgeCheckpointVersionAsync(
        Guid versionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default);

    Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<StudioContentItemListResult> ListContentItemsAsync(StudioContentItemQuery query, CancellationToken cancellationToken = default);

    Task<StudioPackageDraftListResult> ListDraftsAsync(StudioPackageDraftQuery query, CancellationToken cancellationToken = default);

    Task<StudioPublicationRequest> CreatePublicationRequestAsync(
        StudioPublicationRequest request,
        Guid? expectedCurrentVersionId,
        CancellationToken cancellationToken = default);

    Task<StudioPublicationRequest?> GetPublicationRequestAsync(
        Guid itemId,
        Guid versionId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<StudioPublicationRequest?> GetActivePublicationRequestByRouteAsync(
        string route,
        CancellationToken cancellationToken = default);

    Task<StudioRollbackRequest> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default);
}

public sealed class StudioPublicationPointerConflictException : InvalidOperationException
{
    public StudioPublicationPointerConflictException(Guid itemId, Guid? expectedCurrentVersionId, Guid? actualCurrentVersionId)
        : base(
            $"Studio content item '{itemId:D}' advanced past the approved version; " +
            $"expected current version '{Describe(expectedCurrentVersionId)}' but found '{Describe(actualCurrentVersionId)}'. " +
            "Re-validate the publication proposal against the current version and retry.")
    {
        ItemId = itemId;
        ExpectedCurrentVersionId = expectedCurrentVersionId;
        ActualCurrentVersionId = actualCurrentVersionId;
    }

    public Guid ItemId { get; }

    public Guid? ExpectedCurrentVersionId { get; }

    public Guid? ActualCurrentVersionId { get; }

    private static string Describe(Guid? versionId) => versionId?.ToString("D") ?? "(none)";
}

public sealed class StudioPublicationRouteConflictException : InvalidOperationException
{
    public StudioPublicationRouteConflictException(string route, Guid itemId, Guid owningItemId)
        : base($"Route '{route}' is already claimed by another Studio publication.")
    {
        Route = route;
        ItemId = itemId;
        OwningItemId = owningItemId;
    }

    public string Route { get; }

    public Guid ItemId { get; }

    public Guid OwningItemId { get; }
}

public interface IStudioPackageValidator
{
    StudioValidationSummary Validate(StudioPackageEnvelope envelope);

    StudioValidationSummary ValidatePublicationIntent(StudioPublicationIntent? intent);
}

public interface IStudioPackageLifecycleService
{
    StudioPackageFamilyCapabilities GetCapabilities();

    Task<StudioPackageDraft> CreateDraftAsync(CreateStudioPackageDraftCommand command, CancellationToken cancellationToken = default);

    Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    Task<StudioPackageDraft?> UpdateDraftAsync(Guid draftId, UpdateStudioPackageDraftCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    Task<bool> DeleteDraftAsync(Guid draftId, long expectedGeneration, CancellationToken cancellationToken = default);

    Task<StudioValidationSummary?> ValidateDraftAsync(Guid draftId, string? actorId, CancellationToken cancellationToken = default);

    Task<StudioPreviewPlan?> PreviewPlanAsync(Guid draftId, string? actorId, CancellationToken cancellationToken = default);

    Task<StudioContentVersion?> SaveDraftAsVersionAsync(
        Guid draftId,
        string? changeNote,
        string? actorId,
        long? expectedGeneration = null,
        CancellationToken cancellationToken = default);

    Task<StudioContentVersion?> SaveDraftAsCheckpointVersionAsync(
        Guid draftId,
        string? changeNote,
        string? actorId,
        long? expectedGeneration,
        StudioVersionCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<StudioCheckpointedVersion?> GetLatestCheckpointVersionAsync(
        string mapId,
        long afterCursor,
        long throughCursor,
        CancellationToken cancellationToken = default);

    Task AcknowledgeCheckpointVersionAsync(
        Guid versionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<StudioContentItemListResult> ListContentItemsAsync(StudioContentItemQuery query, CancellationToken cancellationToken = default);

    Task<StudioPackageDraftListResult> ListDraftsAsync(StudioPackageDraftQuery query, CancellationToken cancellationToken = default);

    Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default);

    Task<StudioVersionComparison?> CompareVersionsAsync(Guid itemId, Guid leftVersionId, Guid rightVersionId, CancellationToken cancellationToken = default);

    Task<StudioPublicationRequest?> CreatePublicationRequestAsync(
        Guid itemId,
        Guid versionId,
        Guid? expectedCurrentVersionId,
        StudioPublicationIntent? intent,
        string? warningAcknowledgement,
        string? actorId,
        CancellationToken cancellationToken = default);

    Task<StudioPublicationRequest?> CreatePublicationRequestAsync(
        Guid itemId,
        Guid versionId,
        Guid? expectedCurrentVersionId,
        StudioPublicationIntent? intent,
        string? warningAcknowledgement,
        string? actorId,
        Guid publicationRequestId,
        CancellationToken cancellationToken = default)
        => CreatePublicationRequestAsync(
            itemId, versionId, expectedCurrentVersionId, intent, warningAcknowledgement, actorId, cancellationToken);

    Task<StudioPublicationRequest?> GetPublicationRequestAsync(
        Guid itemId,
        Guid versionId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<StudioPublicationRequest?> GetActivePublicationRequestByRouteAsync(
        string route,
        CancellationToken cancellationToken = default);

    Task<StudioPackageDraft?> ReopenVersionAsync(Guid itemId, Guid versionId, string? actorId, CancellationToken cancellationToken = default);

    Task<StudioRollbackRequest?> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default);
}

public sealed record CreateStudioPackageDraftCommand
{
    public Guid? ItemId { get; init; }

    public required string PackageKey { get; init; }

    public string? WorkspaceId { get; init; }

    public string? OwnerId { get; init; }

    public string? ExpectedExistingItemOwnerId { get; init; }

    public bool ExpectedExistingItemPresent { get; init; }

    public required StudioPackageEnvelope Envelope { get; init; }

    public string? ActorId { get; init; }

    public Guid? BaseVersionId { get; init; }
}

public sealed record UpdateStudioPackageDraftCommand
{
    public required string PackageKey { get; init; }

    public string? WorkspaceId { get; init; }

    public string? OwnerId { get; init; }

    public required StudioPackageEnvelope Envelope { get; init; }

    public long? Generation { get; init; }

    public string? ActorId { get; init; }
}
