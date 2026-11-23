// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Scene.Catalog;

internal sealed class SceneCatalogReader : ISceneCatalogReader
{
    private readonly ISceneDatasetRegistry _registry;
    private readonly IOptions<SceneDatasetOptions> _options;
    private readonly ISceneRegistrationService? _registration;

    public SceneCatalogReader(
        ISceneDatasetRegistry registry,
        IOptions<SceneDatasetOptions> options,
        ISceneRegistrationService? registration = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registration = registration;
    }

    public async Task<IReadOnlyList<SceneCatalogEntry>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var scenes = await SceneCatalog
            .ListActiveAsync(_registration, _registry, _options.Value ?? new SceneDatasetOptions(), cancellationToken)
            .ConfigureAwait(false);
        var entries = new SceneCatalogEntry[scenes.Count];
        for (var i = 0; i < scenes.Count; i++)
        {
            entries[i] = SceneCatalog.ToEntry(scenes[i]);
        }

        return entries;
    }

    public async Task<SceneCatalogEntry?> FindActiveAsync(string sceneId, CancellationToken cancellationToken = default)
    {
        var scene = await SceneCatalog
            .FindActiveAsync(sceneId, _registration, _registry, cancellationToken)
            .ConfigureAwait(false);
        return scene is { } resolved ? SceneCatalog.ToEntry(resolved) : null;
    }

    public SceneCatalogResolution ResolveEndpoints(SceneCatalogEntry entry, string baseUrl)
        => SceneCatalog.ResolveEndpoints(entry, baseUrl);

    public IReadOnlyList<SceneCatalogLink> BuildLinks(SceneCatalogEntry entry, string baseUrl)
        => SceneCatalog.BuildLinks(entry, baseUrl);
}
