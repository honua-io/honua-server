// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

/// <summary>
/// Persistence contract for Studio package drafts, immutable versions, and lifecycle requests.
/// </summary>
public interface IStudioPackageStore
{
    /// <summary>Gets the persistence mode backing the store.</summary>
    StudioPackagePersistenceMode PersistenceMode { get; }

    /// <summary>Creates a mutable package draft.</summary>
    Task<StudioPackageDraft> CreateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Gets a mutable package draft by identifier.</summary>
    Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    /// <summary>Updates a mutable package draft.</summary>
    Task<StudioPackageDraft?> UpdateDraftAsync(StudioPackageDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Deletes a mutable package draft.</summary>
    Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    /// <summary>Creates an immutable package content version.</summary>
    Task<StudioContentVersion> CreateVersionAsync(StudioPackageDraft draft, string? changeNote, string? actorId, CancellationToken cancellationToken = default);

    /// <summary>Lists immutable versions for a content item.</summary>
    Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>Gets one immutable content version.</summary>
    Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Gets current and published pointers for a content item.</summary>
    Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>Creates a persisted publication request for an immutable version.</summary>
    Task<StudioPublicationRequest> CreatePublicationRequestAsync(StudioPublicationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates a persisted rollback request and updates item pointers.</summary>
    Task<StudioRollbackRequest> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides package-family capability descriptors.
/// </summary>
public interface IStudioPackageFamilyRegistry
{
    /// <summary>Returns package-family capability data for the current deployment context.</summary>
    StudioPackageFamilyCapabilities GetCapabilities();

    /// <summary>Gets a package-family descriptor, or null when the family is unknown.</summary>
    StudioPackageFamilyDescriptor? GetDescriptor(StudioPackageFamily family);
}

/// <summary>
/// Validates Studio package envelopes.
/// </summary>
public interface IStudioPackageValidator
{
    /// <summary>Validates the supplied envelope.</summary>
    StudioValidationSummary Validate(StudioPackageEnvelope envelope);

    /// <summary>Validates a publication intent override.</summary>
    StudioValidationSummary ValidatePublicationIntent(StudioPublicationIntent? intent);
}

/// <summary>
/// Canonical Studio package lifecycle service.
/// </summary>
public interface IStudioPackageLifecycleService
{
    /// <summary>Gets package-family capability data.</summary>
    StudioPackageFamilyCapabilities GetCapabilities();

    /// <summary>Creates a mutable package draft.</summary>
    Task<StudioPackageDraft> CreateDraftAsync(CreateStudioPackageDraftCommand command, CancellationToken cancellationToken = default);

    /// <summary>Gets a mutable package draft.</summary>
    Task<StudioPackageDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    /// <summary>Updates a mutable package draft.</summary>
    Task<StudioPackageDraft?> UpdateDraftAsync(Guid draftId, UpdateStudioPackageDraftCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes a mutable package draft.</summary>
    Task<bool> DeleteDraftAsync(Guid draftId, CancellationToken cancellationToken = default);

    /// <summary>Validates a mutable package draft and persists the validation summary.</summary>
    Task<StudioValidationSummary?> ValidateDraftAsync(Guid draftId, string? actorId, CancellationToken cancellationToken = default);

    /// <summary>Builds a preview plan for a mutable package draft.</summary>
    Task<StudioPreviewPlan?> PreviewPlanAsync(Guid draftId, string? actorId, CancellationToken cancellationToken = default);

    /// <summary>Saves a mutable package draft as an immutable content version.</summary>
    Task<StudioContentVersion?> SaveDraftAsVersionAsync(Guid draftId, string? changeNote, string? actorId, CancellationToken cancellationToken = default);

    /// <summary>Lists immutable content versions for an item.</summary>
    Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>Gets one immutable content version.</summary>
    Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Compares two immutable content versions.</summary>
    Task<StudioVersionComparison?> CompareVersionsAsync(Guid itemId, Guid leftVersionId, Guid rightVersionId, CancellationToken cancellationToken = default);

    /// <summary>Creates a publication request for an immutable version.</summary>
    Task<StudioPublicationRequest?> CreatePublicationRequestAsync(
        Guid itemId,
        Guid versionId,
        StudioPublicationIntent? intent,
        string? warningAcknowledgement,
        string? actorId,
        CancellationToken cancellationToken = default);

    /// <summary>Reopens an immutable version as a new mutable draft.</summary>
    Task<StudioPackageDraft?> ReopenVersionAsync(Guid itemId, Guid versionId, string? actorId, CancellationToken cancellationToken = default);

    /// <summary>Creates a rollback request and updates item pointers.</summary>
    Task<StudioRollbackRequest?> RollbackAsync(
        Guid itemId,
        Guid targetVersionId,
        StudioRollbackPointer target,
        string? actorId,
        string? reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Command for creating a Studio package draft.
/// </summary>
public sealed record CreateStudioPackageDraftCommand
{
    /// <summary>Optional existing content item id; omitted for a new Studio content item.</summary>
    public Guid? ItemId { get; init; }

    /// <summary>Machine-friendly package key.</summary>
    public required string PackageKey { get; init; }

    /// <summary>Workspace identifier.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Owner principal identifier.</summary>
    public string? OwnerId { get; init; }

    /// <summary>Package envelope.</summary>
    public required StudioPackageEnvelope Envelope { get; init; }

    /// <summary>Identifier of the actor creating the draft.</summary>
    public string? ActorId { get; init; }

    /// <summary>Base immutable version when reopening.</summary>
    public Guid? BaseVersionId { get; init; }
}

/// <summary>
/// Command for updating a Studio package draft.
/// </summary>
public sealed record UpdateStudioPackageDraftCommand
{
    /// <summary>Machine-friendly package key.</summary>
    public required string PackageKey { get; init; }

    /// <summary>Workspace identifier.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Owner principal identifier.</summary>
    public string? OwnerId { get; init; }

    /// <summary>Package envelope.</summary>
    public required StudioPackageEnvelope Envelope { get; init; }

    /// <summary>Expected draft generation for optimistic concurrency.</summary>
    public long? Generation { get; init; }

    /// <summary>Identifier of the actor updating the draft.</summary>
    public string? ActorId { get; init; }
}
