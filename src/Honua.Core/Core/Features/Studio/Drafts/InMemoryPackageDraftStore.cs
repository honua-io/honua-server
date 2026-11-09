// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

/// <summary>
/// Single-process <see cref="IPackageDraftStore"/> (ADR-0076, honua-server#3312).
/// </summary>
/// <remarks>
/// <para>
/// This is the registration used when no shared backing is composed. Each instance
/// owns a private <see cref="MemoryPackageDraftBacking"/>, so two processes — two
/// replicas, or a Lambda cold start — do not see each other's drafts. That is
/// intentional: an in-memory store must not be described as shared. A host that
/// has registered a Redis connection uses <c>RedisPackageDraftStore</c> instead,
/// and two of those instances read what the other wrote.
/// </para>
/// <para>
/// Tests that need the replica contract pass one <see cref="IPackageDraftBacking"/>
/// into both instances. Retention stays the bounds in
/// <see cref="PackageDraftRetentionOptions"/>: default 24 hours, 500 drafts per
/// kind, oldest write evicted first. An expired draft and a draft owned by
/// someone else both read as not found.
/// </para>
/// </remarks>
public sealed class InMemoryPackageDraftStore : IPackageDraftStore
{
    private readonly PackageDraftLedger _ledger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryPackageDraftStore"/> class
    /// with a private backing.
    /// </summary>
    /// <param name="retention">Age and capacity bounds.</param>
    /// <param name="timeProvider">Clock used for expiry, injected so tests can pin it.</param>
    public InMemoryPackageDraftStore(PackageDraftRetentionOptions retention, TimeProvider timeProvider)
        : this(retention, timeProvider, new MemoryPackageDraftBacking(timeProvider))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryPackageDraftStore"/> class
    /// over a caller-supplied backing. Two instances constructed with the same backing
    /// are the replica case.
    /// </summary>
    /// <param name="retention">Age and capacity bounds.</param>
    /// <param name="timeProvider">Clock stamped onto each write.</param>
    /// <param name="backing">Shared or private record store.</param>
    internal InMemoryPackageDraftStore(
        PackageDraftRetentionOptions retention,
        TimeProvider timeProvider,
        IPackageDraftBacking backing)
    {
        _ledger = new PackageDraftLedger(backing, retention, timeProvider);
    }

    /// <inheritdoc />
    public Task SaveMapDraftAsync(MapPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveMapDraftAsync(package, ownerId, cancellationToken);

    /// <inheritdoc />
    public Task SaveAppDraftAsync(AppPackage package, string ownerId, CancellationToken cancellationToken = default) =>
        _ledger.SaveAppDraftAsync(package, ownerId, cancellationToken);

    /// <inheritdoc />
    public Task<MapPackage?> GetMapDraftAsync(
        string mapPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetMapDraftAsync(mapPackageId, ownerId, cancellationToken);

    /// <inheritdoc />
    public Task<AppPackage?> GetAppDraftAsync(
        string appPackageId,
        string ownerId,
        CancellationToken cancellationToken = default) =>
        _ledger.GetAppDraftAsync(appPackageId, ownerId, cancellationToken);
}
