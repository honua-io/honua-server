// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

/// <summary>
/// Admin-side CRUD seam for registered scene datasets. The hosted serving
/// path (#837) reads the same store via <see cref="ISceneDatasetRegistry"/>;
/// this interface exposes the lifecycle operations that drive admin tooling.
/// </summary>
public interface ISceneRegistrationService
{
    /// <summary>
    /// Persists a new registry entry. Implementations should surface duplicate
    /// <see cref="SceneDatasetRecord.Id"/> or <see cref="SceneDatasetRecord.Name"/>
    /// collisions as <see cref="SceneDatasetAlreadyExistsException"/>.
    /// </summary>
    Task<SceneDatasetRecord> RegisterAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a record by its database primary key.
    /// </summary>
    Task<SceneDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a record by its scene URL slug.
    /// </summary>
    Task<SceneDatasetRecord?> GetBySceneIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists registry entries. Inactive entries are excluded by default.
    /// </summary>
    Task<IReadOnlyList<SceneDatasetRecord>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing record and bumps its revision counter.
    /// </summary>
    Task<SceneDatasetRecord> UpdateAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the record as <see cref="SceneDatasetStatus.Inactive"/>.
    /// Returns false when no record matches the supplied id.
    /// </summary>
    Task<bool> DeactivateAsync(Guid datasetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown by <see cref="ISceneRegistrationService"/> implementations when an
/// inserted or updated record collides with an existing slug or display name.
/// </summary>
public sealed class SceneDatasetAlreadyExistsException : Exception
{
    /// <summary>
    /// Initializes a new instance with the supplied message.
    /// </summary>
    public SceneDatasetAlreadyExistsException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the supplied message and inner exception.
    /// </summary>
    public SceneDatasetAlreadyExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
