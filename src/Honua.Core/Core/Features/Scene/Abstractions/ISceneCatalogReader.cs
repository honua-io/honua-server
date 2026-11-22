// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Abstractions;

public interface ISceneCatalogReader
{
    Task<IReadOnlyList<SceneCatalogEntry>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<SceneCatalogEntry?> FindActiveAsync(string sceneId, CancellationToken cancellationToken = default);

    SceneCatalogResolution ResolveEndpoints(SceneCatalogEntry entry, string baseUrl);

    IReadOnlyList<SceneCatalogLink> BuildLinks(SceneCatalogEntry entry, string baseUrl);
}
