// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;

namespace Honua.Core.Features.Scene.Catalog;

/// <summary>
/// A single resolved scene from the shared discovery catalog. Exactly one of
/// <see cref="Record"/> or <see cref="Scene"/> is non-null: database-backed
/// scenes surface as a <see cref="SceneDatasetRecord"/>, configuration-only
/// scenes as a <see cref="SceneDataset"/>.
/// </summary>
/// <param name="Record">The database-backed registry record, when present.</param>
/// <param name="Scene">The configuration-backed dataset, when present.</param>
public readonly record struct ResolvedScene(SceneDatasetRecord? Record, SceneDataset? Scene)
{
    /// <summary>The scene's stable url identifier, regardless of source.</summary>
    public string Id => Record?.Id ?? Scene!.Id;
}

/// <summary>
/// Shared scene-discovery catalog enumeration. Both the public HTTP discovery
/// surface (<c>SceneDiscoveryEndpoints.HandleListScenes</c>) and the gRPC
/// <c>SceneService.ListScenes</c> adapter consume this single helper so the
/// active-scene set, the database-over-configuration dedup precedence, and the
/// ordering stay identical across protocols (per the CLAUDE.md DRY / shared
/// catalog rules). Each adapter maps the returned <see cref="ResolvedScene"/>
/// sequence into its own wire DTO and applies only protocol-specific projection
/// (capability filtering, spatial-extent filtering, paging).
/// </summary>
public static class SceneCatalog
{
    /// <summary>
    /// Enumerates the active, deduped, ordered scene catalog.
    /// </summary>
    /// <remarks>
    /// Behavior (identical to the prior per-adapter copies):
    /// <list type="number">
    /// <item>Lists every registration record (<c>includeInactive: true</c>) when
    /// a <paramref name="registration"/> service is present, recording each id as
    /// seen so configuration entries never shadow a database scene.</item>
    /// <item>Emits only <see cref="SceneDatasetStatus.Active"/> records — inactive
    /// scenes are deduped (their id is reserved) but not returned.</item>
    /// <item>Folds in configuration datasets via
    /// <see cref="ISceneDatasetRegistry.FindAsync"/>, skipping blank or
    /// already-seen ids.</item>
    /// <item>Orders the result by id using <see cref="StringComparer.Ordinal"/>.</item>
    /// </list>
    /// </remarks>
    /// <param name="registration">
    /// Optional admin registry service (present only on database-backed profiles).
    /// </param>
    /// <param name="registry">Configuration-backed scene lookup.</param>
    /// <param name="options">Configuration-defined scene datasets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
