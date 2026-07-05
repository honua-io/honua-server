// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

/// <summary>
/// Read-through materialization seam for hosted 3D Tiles scene assets (#2459,
/// ADR-0060). A scene published on one node uploads its promoted tileset tree to
/// the shared object store under <see cref="SceneDatasetRecord.AssetStoragePrefix"/>;
/// any node can then hydrate a local materialization cache under the record's
/// asset root before serving so the scene is servable cluster-wide.
/// </summary>
/// <remarks>
/// Implementations are invoked on the serving path (before asset resolution) and
/// must be a fast no-op once a scene's local cache is present and current. Records
/// with no <see cref="SceneDatasetRecord.AssetStoragePrefix"/> (legacy,
/// filesystem-only datasets) are skipped entirely and keep serving straight off
/// local disk.
/// </remarks>
public interface ISceneAssetHydrator
{
    /// <summary>
    /// Ensures the scene's asset tree is materialized locally under
    /// <paramref name="localAssetRoot"/>. When the record carries a storage prefix
    /// and the local cache is missing or stale, the tree is downloaded from the
    /// shared object store and atomically installed. When the record has no storage
    /// prefix, or the local cache is already current, this is a fast no-op.
    /// </summary>
    /// <param name="record">The registry record being resolved for serving.</param>
    /// <param name="localAssetRoot">
    /// The canonicalized local directory the serving path resolves assets under
    /// (mirrors <see cref="SceneDataset.AssetRoot"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task EnsureLocalAsync(
        SceneDatasetRecord record,
        string localAssetRoot,
        CancellationToken cancellationToken = default);
}
