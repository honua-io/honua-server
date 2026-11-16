// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;

namespace Honua.Core.Features.Scene.Catalog;

public readonly record struct ResolvedScene(SceneDatasetRecord? Record, SceneDataset? Scene)
{
    public string Id => Record?.Id ?? Scene!.Id;
}

public static class SceneCatalog
{
    public static async Task<IReadOnlyList<ResolvedScene>> ListActiveAsync(
        ISceneRegistrationService? registration,
        ISceneDatasetRegistry registry,
        SceneDatasetOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        var resolved = new List<ResolvedScene>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (registration is not null)
        {
            var records = await registration
                .ListAsync(includeInactive: true, cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                seen.Add(record.Id);
                if (record.Status == SceneDatasetStatus.Active)
                {
                    resolved.Add(new ResolvedScene(record, Scene: null));
                }
            }
        }

        foreach (var entry in options.Datasets ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id))
            {
                continue;
            }

            var scene = await registry.FindAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            if (scene is not null)
            {
                resolved.Add(new ResolvedScene(Record: null, Scene: scene));
            }
        }

        resolved.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return resolved;
    }
}
