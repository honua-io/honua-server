// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

public interface ISceneRegistrationService
{
    Task<SceneDatasetRecord> RegisterAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default);

    Task<SceneDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default);

    Task<SceneDatasetRecord?> GetBySceneIdAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SceneDatasetRecord>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<SceneDatasetRecord> UpdateAsync(SceneDatasetRecord record, CancellationToken cancellationToken = default);

    Task<bool> DeactivateAsync(Guid datasetId, CancellationToken cancellationToken = default);
}

public sealed class SceneDatasetAlreadyExistsException : Exception
{
    public SceneDatasetAlreadyExistsException(string message)
        : base(message)
    {
    }

    public SceneDatasetAlreadyExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
