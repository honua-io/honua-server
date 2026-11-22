// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Scene;
using Honua.Scene;
using Honua.Scene.Assets;
using AccessPolicy = Honua.Core.Features.Security.Domain.AccessPolicy;

namespace Honua.Core.Features.Scene.Catalog;

public readonly record struct ResolvedScene(SceneDatasetRecord? Record, SceneDataset? Scene)
{
    public string Id => Record?.Id ?? Scene!.Id;

    public string Name => Record?.Name ?? Scene!.Name;

    public string? Description => Record is { } record ? record.Description : Scene?.Description;

    public SceneServingFormat ServingFormat => Record is { } record
        ? SceneServingProjection.ResolveServingFormat(record)
        : SceneServingProjection.ResolveServingFormat(Scene!);

    public SceneContentKind ContentKind => Record is { } record
        ? SceneServingProjection.ResolveContentKind(record)
        : SceneServingProjection.ResolveContentKind(Scene!);

    public SceneExtent? Extent => Record?.Extent;

    public string? Crs => Record?.Crs;

    public string TilesetFileName => Record?.TilesetFileName ?? Scene!.TilesetFileName;

    public DateTimeOffset? UpdatedAt => Record is { } record
        ? record.UpdatedAt ?? record.CreatedAt
        : null;

    public AccessPolicy? AccessPolicy => Record is { } record
        ? SceneServingProjection.ToAccessPolicy(record)
        : Scene!.AccessPolicy;
}

public static class SceneCatalog
{
    private static readonly string[] ProtectedSchemes = ["Bearer", "ApiKey"];
    private static readonly string[] ThreeDTilesCapabilities = [SceneServingProjection.ThreeDTilesWireFormat];
    private static readonly string[] CopcCapabilities = [SceneServingProjection.CopcWireFormat];

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

    public static async Task<ResolvedScene?> FindActiveAsync(
        string sceneId,
        ISceneRegistrationService? registration,
        ISceneDatasetRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return null;
        }

        if (registration is not null)
        {
            var record = await registration.GetBySceneIdAsync(sceneId, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                return record.Status == SceneDatasetStatus.Active
                    ? new ResolvedScene(record, Scene: null)
                    : null;
            }
        }

        var scene = await registry.FindAsync(sceneId, cancellationToken).ConfigureAwait(false);
        return scene is null ? null : new ResolvedScene(Record: null, Scene: scene);
    }

    public static SceneCatalogEntry ToEntry(ResolvedScene resolved)
        => new()
        {
            Id = resolved.Id,
            Name = resolved.Name,
            Description = resolved.Description,
            ServingFormat = resolved.ServingFormat,
            ContentKind = resolved.ContentKind,
            Extent = resolved.Extent,
            Crs = resolved.Crs,
            TilesetFileName = resolved.TilesetFileName,
            AccessPolicy = resolved.AccessPolicy,
            UpdatedAt = resolved.UpdatedAt
        };

    public static SceneCatalogEntry ToEntry(SceneDatasetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ToEntry(new ResolvedScene(record, Scene: null));
    }

    public static SceneCatalogEntry ToEntry(SceneDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return ToEntry(new ResolvedScene(Record: null, Scene: dataset));
    }

    public static SceneCatalogResolution ResolveEndpoints(SceneCatalogEntry entry, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(baseUrl);

        var requiresAuth = entry.RequiresAuthentication;
        var accessSession = requiresAuth
            ? new SceneCatalogAccessSessionAffordance(SceneUrls.AbsoluteAccessEnvelopeUrl(baseUrl, entry.Id), "POST")
            : null;
        var schemes = requiresAuth ? ProtectedSchemes : [];

        return entry.ServingFormat switch
        {
            SceneServingFormat.Copc => BuildCopcResolution(entry, baseUrl, requiresAuth, accessSession, schemes),
            SceneServingFormat.ThreeDTiles => BuildThreeDTilesResolution(entry, baseUrl, requiresAuth, accessSession, schemes),
            _ => BuildSuppressedResolution(entry, requiresAuth, schemes)
        };
    }

    public static IReadOnlyList<SceneCatalogLink> BuildLinks(SceneCatalogEntry entry, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(baseUrl);

        return
        [
            new SceneCatalogLink(
                Rel: "self",
                Href: SceneUrls.AbsoluteSceneApiUrl(baseUrl, entry.Id),
                Type: SceneContentTypes.JsonContentType,
                Title: "Scene metadata"),
            new SceneCatalogLink(
                Rel: "resolve",
                Href: SceneUrls.AbsoluteSceneResolveUrl(baseUrl, entry.Id),
                Type: SceneContentTypes.JsonContentType,
                Title: "Scene render endpoint resolution")
        ];
    }

    public static IReadOnlyList<string> CapabilitiesFor(SceneServingFormat servingFormat) => servingFormat switch
    {
        SceneServingFormat.ThreeDTiles => ThreeDTilesCapabilities,
        SceneServingFormat.Copc => CopcCapabilities,
        _ => [SceneServingProjection.ToWireValue(servingFormat)]
    };

    private static SceneCatalogResolution BuildThreeDTilesResolution(
        SceneCatalogEntry entry,
        string baseUrl,
        bool requiresAuth,
        SceneCatalogAccessSessionAffordance? accessSession,
        string[] schemes)
    {
        var tilesetUrl = SceneUrls.AbsoluteTilesetUrl(baseUrl, entry.Id);
        return new SceneCatalogResolution
        {
            SceneId = entry.Id,
            ServingFormat = entry.ServingFormat,
            ContentKind = entry.ContentKind,
            TilesetUrl = tilesetUrl,
            Capabilities = ThreeDTilesCapabilities,
            RequiresAuthentication = requiresAuth,
            AuthSchemes = schemes,
            Endpoints =
            [
                new SceneCatalogEndpoint
                {
                    Kind = SceneServingProjection.ThreeDTilesWireFormat,
                    EndpointFormat = SceneServingProjection.ThreeDTilesWireFormat,
                    Url = tilesetUrl,
                    MediaType = SceneContentTypes.JsonContentType,
                    RequiresAuthentication = requiresAuth,
                    AccessSession = accessSession
                }
            ]
        };
    }

    private static SceneCatalogResolution BuildCopcResolution(
        SceneCatalogEntry entry,
        string baseUrl,
        bool requiresAuth,
        SceneCatalogAccessSessionAffordance? accessSession,
        string[] schemes)
    {
        var copcUrl = SceneUrls.AbsoluteCopcUrl(baseUrl, entry.Id);
        return new SceneCatalogResolution
        {
            SceneId = entry.Id,
            ServingFormat = entry.ServingFormat,
            ContentKind = entry.ContentKind,
            TilesetUrl = null,
            Capabilities = CopcCapabilities,
            RequiresAuthentication = requiresAuth,
            AuthSchemes = schemes,
            Endpoints =
            [
                new SceneCatalogEndpoint
                {
                    Kind = SceneServingProjection.CopcWireFormat,
                    EndpointFormat = SceneServingProjection.CopcWireFormat,
                    Url = copcUrl,
                    MediaType = SceneContentTypes.CopcContentType,
                    RequiresAuthentication = requiresAuth,
                    AccessSession = accessSession
                }
            ]
        };
    }

    private static SceneCatalogResolution BuildSuppressedResolution(
        SceneCatalogEntry entry,
        bool requiresAuth,
        string[] schemes)
        => new()
        {
            SceneId = entry.Id,
            ServingFormat = entry.ServingFormat,
            ContentKind = entry.ContentKind,
            TilesetUrl = null,
            Capabilities = [SceneServingProjection.ToWireValue(entry.ServingFormat)],
            RequiresAuthentication = requiresAuth,
            AuthSchemes = schemes,
            Endpoints = []
        };
}
