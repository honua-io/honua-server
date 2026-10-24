// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

/// <summary>
/// Lookup seam for hosted 3D Tiles scene datasets. The initial implementation
/// is configuration-backed; later work (registry CRUD, signed access) replaces
/// the implementation without changing the contract.
/// </summary>
public interface ISceneDatasetRegistry
{
    /// <summary>
    /// Resolves a scene dataset by its identifier.
    /// </summary>
    /// <param name="id">Scene identifier as it appears in the request URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching <see cref="SceneDataset"/>, or <c>null</c> if no scene with that id is registered.</returns>
    ValueTask<SceneDataset?> FindAsync(string id, CancellationToken cancellationToken = default);
}
