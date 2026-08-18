// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Server-side home for freshly created <see cref="MapPackage"/> and
/// <see cref="AppPackage"/> drafts, keyed by the <c>map_…</c> / <c>app_…</c>
/// identifier the draft factories mint (ADR-0076, honua-server#3262).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0076 says the deterministic entry points "create <b>and persist</b> a
/// draft package" and return an identifier "addressable at its
/// <c>honua://map-packages/{id}</c> / <c>honua://app-packages/{id}</c> URI". The
/// first implementation created without persisting: the package resources
/// reverse-look-up <i>deployments</i>, and a fresh draft has none, so the URI the
/// tool handed back never resolved.
/// </para>
/// <para>
/// This is the missing surface, and it is deliberately its own one rather than
/// <c>IStudioPackageStore</c>. The Studio store is <see cref="Guid"/>-keyed over
/// a <c>StudioPackageEnvelope</c>; routing drafts through it would put two
/// competing identifier schemes on the same object, and the identifier the tool
/// returns — the one the URI is built from — would not be the store's key. Here
/// the minted identifier <i>is</i> the key, so create-then-resolve is a single
/// lookup with nothing to reconcile.
/// </para>
/// <para>
/// A draft is pre-publish scratch: it becomes durable when it is promoted to a
/// deployment, which is the surface the package resources already read. Retention
/// here is therefore explicitly bounded rather than indefinite — see
/// <see cref="PackageDraftRetentionOptions"/>.
/// </para>
/// </remarks>
public interface IPackageDraftStore
{
    /// <summary>Records a map-package draft under its <see cref="MapPackage.MapPackageId"/>.</summary>
    /// <param name="package">The draft to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveMapDraftAsync(MapPackage package, CancellationToken cancellationToken = default);

    /// <summary>Records an app-package draft under its <see cref="AppPackage.AppPackageId"/>.</summary>
    /// <param name="package">The draft to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAppDraftAsync(AppPackage package, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a recorded map-package draft, or <see langword="null"/> when no draft
    /// with that identifier is held (never created, or retention expired).
    /// </summary>
    /// <param name="mapPackageId">The <c>map_…</c> identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MapPackage?> GetMapDraftAsync(string mapPackageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a recorded app-package draft, or <see langword="null"/> when no draft
    /// with that identifier is held (never created, or retention expired).
    /// </summary>
    /// <param name="appPackageId">The <c>app_…</c> identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AppPackage?> GetAppDraftAsync(string appPackageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounds on how long and how many unpromoted package drafts a
/// <see cref="IPackageDraftStore"/> holds.
/// </summary>
/// <remarks>
/// Both bounds exist because a draft store is a write surface reachable by any
/// caller authorized to create a package: without a cap it is an unbounded
/// allocation driven by request volume. Eviction is by age, so a draft that is
/// still being composed is the last thing dropped.
/// </remarks>
public sealed record PackageDraftRetentionOptions
{
    /// <summary>
    /// How long a draft stays resolvable after it is created. Default 24 hours:
    /// long enough for a composition session, short enough that abandoned drafts
    /// do not accumulate.
    /// </summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum drafts held per package kind. When exceeded, the oldest drafts are
    /// evicted first. Default 500.
    /// </summary>
    public int Capacity { get; init; } = 500;
}
