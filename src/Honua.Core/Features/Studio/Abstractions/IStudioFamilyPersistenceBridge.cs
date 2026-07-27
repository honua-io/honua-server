// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

/// <summary>
/// Adapter that lets the Studio package lifecycle enumerate, open, and save one package family
/// by delegating persistence to that family's native store instead of the Studio store
/// (ADR-0069, honua-server#3004). The native store stays the single source of truth: reads are
/// live projections of native rows and saves write through the native store's own API, so the
/// family's domain semantics (form ETag/version model, analysis artifact/job links) are
/// preserved and console's native editors keep working with no flag-day.
/// </summary>
public interface IStudioFamilyPersistenceBridge
{
    /// <summary>Package family served by this bridge.</summary>
    StudioPackageFamily Family { get; }

    /// <summary>Native package format advertised for the family (for example <c>honua.form-package.v1</c>).</summary>
    string Format { get; }

    /// <summary>Whether lifecycle publish-requests are supported for this family.</summary>
    bool PublishSupported { get; }

    /// <summary>Lifecycle operations supported for this family.</summary>
    IReadOnlyList<StudioPackageOperation> SupportedOperations { get; }

    /// <summary>Client-facing limitations advertised through <c>GET /package-families</c>.</summary>
    IReadOnlyList<string> Limitations { get; }

    /// <summary>
    /// Lists native items as Studio content-item summaries. Implementations bound the result
    /// (at most <see cref="StudioFamilyPersistenceBridgeDefaults.MaxEnumeratedItems"/> rows);
    /// filtering, ordering, and cursor pagination are applied by the caller.
    /// </summary>
    Task<IReadOnlyList<StudioContentItemSummary>> ListItemSummariesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves current/published pointers for a bridged item, or <see langword="null"/> when
    /// the item id does not map to a native record of this family.
    /// </summary>
    Task<StudioContentItemPointers?> GetPointersAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists native versions for a bridged item as immutable Studio content versions ordered by
    /// version number. Returns an empty list when the item id does not map to a native record.
    /// </summary>
    Task<IReadOnlyList<StudioContentVersion>> ListVersionsAsync(Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one native version projected as an immutable Studio content version, or
    /// <see langword="null"/> when the item or version does not resolve.
    /// </summary>
    Task<StudioContentVersion?> GetVersionAsync(Guid itemId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the draft's envelope through to the native store as a new native version and
    /// returns its Studio content-version projection. Throws <see cref="ArgumentException"/>
    /// when the envelope body is not a valid native document for the family.
    /// </summary>
    Task<StudioContentVersion> SaveVersionAsync(
        StudioPackageDraft draft,
        string? changeNote,
        string? actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a lifecycle publish-request against the native store. Implementations that do
    /// not support publication throw <see cref="InvalidOperationException"/> (surfaced as
    /// <c>409 Conflict</c>). Implementations that do support it return the stored request with
    /// its final status (<c>accepted</c>, or <c>rejected</c> when native validation fails).
    /// </summary>
    Task<StudioPublicationRequest> PublishAsync(StudioPublicationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared bounds for Studio family persistence bridges.
/// </summary>
public static class StudioFamilyPersistenceBridgeDefaults
{
    /// <summary>
    /// Maximum native rows a bridge merges into content-item enumeration (documented
    /// limitation, ADR-0069).
    /// </summary>
    public const int MaxEnumeratedItems = 1_000;
}
