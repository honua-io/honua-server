// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Scene;
using Honua.Protocols.Scene.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Scene;

/// <summary>
/// Public SDK-compatible scene discovery endpoints. Management stays on
/// /api/v1/admin/scenes; these routes only advertise renderable scene metadata.
/// </summary>
internal static partial class SceneDiscoveryEndpoints
{
    private const string ScenesTag = "Scenes";
    private const string ThreeDimensionalTilesCapability = "3d-tiles";
    private const string HostedTilesDatasetType = "hosted_tiles";
    private static readonly string[] HostedTilesCapabilities = [ThreeDimensionalTilesCapability];
    private static readonly string[] ProtectedSchemes = ["Bearer", "ApiKey"];

    public static IEndpointRouteBuilder MapSceneDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/scenes", HandleListScenes)
            .WithName("ListPublicScenes")
            .WithDisplayName("List Public Scene Discovery Metadata")
            .WithSummary("List hosted scenes visible to SDK clients")
            .WithDescription("Returns SDK-compatible scene summaries for public/mobile discovery without exposing admin management operations.")
            .WithTags(ScenesTag)
            .Produces<PublicSceneListResponse>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status500InternalServerError);

        endpoints.MapGet("/api/scenes/{sceneId}/resolve", HandleResolveScene)
            .WithName("ResolvePublicScene")
            .WithDisplayName("Resolve Public Scene Render Endpoints")
            .WithSummary("Resolve a hosted scene into render-ready endpoint URLs")
            .WithDescription("Returns SDK-compatible render endpoint metadata for a hosted scene.")
            .WithTags(ScenesTag)
            .Produces<PublicSceneResolution>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        endpoints.MapGet("/api/scenes/{sceneId}", HandleGetScene)
            .WithName("GetPublicScene")
            .WithDisplayName("Get Public Scene Discovery Metadata")
            .WithSummary("Get SDK-compatible metadata for a hosted scene")
            .WithDescription("Returns public/mobile discovery metadata for a hosted scene without exposing admin management fields.")
            .WithTags(ScenesTag)
            .Produces<PublicSceneMetadata>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> HandleListScenes(
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] IOptions<SceneDatasetOptions> options,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Scene.SceneDiscoveryEndpoints");

        try
        {
            var includeInactive = ParseBoolean(context.Request.Query["includeDisabled"].ToString());
            var requestedCapabilities = ParseRequestedCapabilities(context.Request.Query["capabilities"].ToString());
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var summaries = new List<PublicSceneSummary>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var registration = context.RequestServices.GetService<ISceneRegistrationService>();
            if (registration is not null)
            {
                var records = await registration.ListAsync(includeInactive: true, cancellationToken).ConfigureAwait(false);
                foreach (var record in records)
                {
                    seen.Add(record.Id);

                    if (!includeInactive && record.Status != SceneDatasetStatus.Active)
                    {
                        continue;
                    }

                    if (!MatchesCapabilities(requestedCapabilities, record.DatasetType))
                    {
                        continue;
                    }

                    summaries.Add(ToSummary(record, baseUrl));
                }
            }

            foreach (var entry in options.Value?.Datasets ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id))
                {
                    continue;
                }

                if (!MatchesCapabilities(requestedCapabilities, SceneDatasetType.HostedTiles))
                {
                    continue;
                }

                var scene = await registry.FindAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                if (scene is null)
                {
                    continue;
                }

                summaries.Add(ToSummary(scene, baseUrl));
            }

            var response = new PublicSceneListResponse
            {
                Scenes = summaries
                    .OrderBy(scene => scene.Id, StringComparer.Ordinal)
                    .ToArray()
            };

            return Results.Json(response, PublicSceneDiscoveryJsonContext.Default.PublicSceneListResponse);
        }
        catch (Exception ex)
        {
            Log.SceneDiscoveryFailed(logger, "list", ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to list scenes.");
        }
    }

    private static async Task<IResult> HandleGetScene(
        string sceneId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Scene.SceneDiscoveryEndpoints");

        try
        {
            var resolved = await ResolveSceneForDiscoveryAsync(
                sceneId,
                context,
                registry,
                cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Scene was not found.");
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var response = resolved.Record is not null
                ? ToMetadata(resolved.Record, baseUrl)
                : ToMetadata(resolved.Scene!, baseUrl);

            return Results.Json(response, PublicSceneDiscoveryJsonContext.Default.PublicSceneMetadata);
        }
        catch (Exception ex)
        {
            Log.SceneDiscoveryFailed(logger, "get", ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to get scene.");
        }
    }

    private static async Task<IResult> HandleResolveScene(
        string sceneId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Scene.SceneDiscoveryEndpoints");

        try
        {
            var resolved = await ResolveSceneForDiscoveryAsync(
                sceneId,
                context,
                registry,
                cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "Scene was not found.");
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var response = resolved.Record is not null
                ? ToResolution(resolved.Record, baseUrl)
                : ToResolution(resolved.Scene!, baseUrl);

            return Results.Json(response, PublicSceneDiscoveryJsonContext.Default.PublicSceneResolution);
        }
        catch (Exception ex)
        {
            Log.SceneDiscoveryFailed(logger, "resolve", ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to resolve scene.");
        }
    }

    private static async Task<SceneDiscoveryLookup?> ResolveSceneForDiscoveryAsync(
        string sceneId,
        HttpContext context,
        ISceneDatasetRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return null;
        }

        var registration = context.RequestServices.GetService<ISceneRegistrationService>();
        if (registration is not null)
        {
            var record = await registration.GetBySceneIdAsync(sceneId, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                return record.Status == SceneDatasetStatus.Active
                    ? new SceneDiscoveryLookup(record, Scene: null)
                    : null;
            }
        }

        var scene = await registry.FindAsync(sceneId, cancellationToken).ConfigureAwait(false);
        return scene is null ? null : new SceneDiscoveryLookup(Record: null, Scene: scene);
    }

    private static PublicSceneSummary ToSummary(SceneDatasetRecord record, string baseUrl)
    {
        var auth = ToAuth(record);

        return new PublicSceneSummary
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            TilesetUrl = BuildTilesetUrl(baseUrl, record.Id),
            Bounds = ToBounds(record.Extent),
            Capabilities = HostedTilesCapabilities,
            Auth = auth,
            UpdatedAt = record.UpdatedAt ?? record.CreatedAt
        };
    }

    private static PublicSceneSummary ToSummary(SceneDataset scene, string baseUrl)
    {
        return new PublicSceneSummary
        {
            Id = scene.Id,
            Name = scene.Name,
            Description = scene.Description,
            TilesetUrl = BuildTilesetUrl(baseUrl, scene.Id),
            Capabilities = HostedTilesCapabilities,
            Auth = ToAuth(scene)
        };
    }

    private static PublicSceneMetadata ToMetadata(SceneDatasetRecord record, string baseUrl)
    {
        var tilesetUrl = BuildTilesetUrl(baseUrl, record.Id);
        var auth = ToAuth(record);
        var bounds = ToBounds(record.Extent);

        return new PublicSceneMetadata
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            Tileset = ToTilesetEndpoint(tilesetUrl, auth.RequiresAuthentication),
            Center = ToCenter(bounds),
            Bounds = bounds,
            Capabilities = HostedTilesCapabilities,
            Auth = auth,
            Links =
            [
                new PublicSceneLink
                {
                    Rel = "self",
                    Href = BuildSceneApiUrl(baseUrl, record.Id),
                    Type = "application/json",
                    Title = "Scene metadata"
                },
                new PublicSceneLink
                {
                    Rel = "resolve",
                    Href = BuildSceneResolveUrl(baseUrl, record.Id),
                    Type = "application/json",
                    Title = "Scene render endpoint resolution"
                }
            ],
            UpdatedAt = record.UpdatedAt ?? record.CreatedAt
        };
    }

    private static PublicSceneMetadata ToMetadata(SceneDataset scene, string baseUrl)
    {
        var tilesetUrl = BuildTilesetUrl(baseUrl, scene.Id);
        var auth = ToAuth(scene);

        return new PublicSceneMetadata
        {
            Id = scene.Id,
            Name = scene.Name,
            Description = scene.Description,
            Tileset = ToTilesetEndpoint(tilesetUrl, auth.RequiresAuthentication),
            Capabilities = HostedTilesCapabilities,
            Auth = auth,
            Links =
            [
                new PublicSceneLink
                {
                    Rel = "self",
                    Href = BuildSceneApiUrl(baseUrl, scene.Id),
                    Type = "application/json",
                    Title = "Scene metadata"
                },
                new PublicSceneLink
                {
                    Rel = "resolve",
                    Href = BuildSceneResolveUrl(baseUrl, scene.Id),
                    Type = "application/json",
                    Title = "Scene render endpoint resolution"
                }
            ]
        };
    }

    private static PublicSceneResolution ToResolution(SceneDatasetRecord record, string baseUrl)
    {
        var auth = ToAuth(record);
        return ToResolution(record.Id, BuildTilesetUrl(baseUrl, record.Id), auth);
    }

    private static PublicSceneResolution ToResolution(SceneDataset scene, string baseUrl)
    {
        var auth = ToAuth(scene);
        return ToResolution(scene.Id, BuildTilesetUrl(baseUrl, scene.Id), auth);
    }

    private static PublicSceneResolution ToResolution(
        string sceneId,
        string tilesetUrl,
        PublicSceneAuthRequirements auth)
    {
        return new PublicSceneResolution
        {
            SceneId = sceneId,
            TilesetUrl = tilesetUrl,
            Capabilities = HostedTilesCapabilities,
            Auth = auth,
            Endpoints =
            [
                ToTilesetEndpoint(tilesetUrl, auth.RequiresAuthentication)
            ]
        };
    }

    private static PublicSceneEndpoint ToTilesetEndpoint(string tilesetUrl, bool requiresAuthentication)
        => new()
        {
            Kind = ThreeDimensionalTilesCapability,
            Url = tilesetUrl,
            Format = ThreeDimensionalTilesCapability,
            MediaType = SceneContentTypes.JsonContentType,
            RequiresAuthentication = requiresAuthentication
        };

    private static PublicSceneAuthRequirements ToAuth(SceneDatasetRecord record)
    {
        var requiresAuthentication = record.RequiresAuth || !record.IsPublic;
        return new PublicSceneAuthRequirements
        {
            RequiresAuthentication = requiresAuthentication,
            Schemes = requiresAuthentication ? ProtectedSchemes : [],
            Policy = requiresAuthentication && record.AllowedRoles is { Count: > 0 }
                ? string.Join(',', record.AllowedRoles)
                : null
        };
    }

    private static PublicSceneAuthRequirements ToAuth(SceneDataset scene)
    {
        var requiresAuthentication = scene.AccessPolicy is not null;
        return new PublicSceneAuthRequirements
        {
            RequiresAuthentication = requiresAuthentication,
            Schemes = requiresAuthentication ? ProtectedSchemes : []
        };
    }

    private static PublicSceneBounds? ToBounds(SceneExtent? extent)
        => extent is null
            ? null
            : new PublicSceneBounds
            {
                West = extent.XMin,
                South = extent.YMin,
                East = extent.XMax,
                North = extent.YMax
            };

    private static PublicSceneCoordinate? ToCenter(PublicSceneBounds? bounds)
        => bounds is null
            ? null
            : new PublicSceneCoordinate
            {
                Latitude = (bounds.South + bounds.North) / 2,
                Longitude = (bounds.West + bounds.East) / 2,
                Height = 1200
            };

    private static string BuildSceneApiUrl(string baseUrl, string sceneId)
        => BuildUrl(baseUrl, "/api/scenes/", sceneId, suffix: null);

    private static string BuildSceneResolveUrl(string baseUrl, string sceneId)
        => BuildUrl(baseUrl, "/api/scenes/", sceneId, "/resolve");

    private static string BuildTilesetUrl(string baseUrl, string sceneId)
        => BuildUrl(baseUrl, "/scenes/", sceneId, "/tileset.json");

    private static string BuildUrl(string baseUrl, string prefix, string sceneId, string? suffix)
    {
        var escapedSceneId = Uri.EscapeDataString(sceneId);
        return string.Concat(
            baseUrl.AsSpan().TrimEnd('/'),
            prefix.AsSpan(),
            escapedSceneId.AsSpan(),
            suffix.AsSpan());
    }

    private static string[] ParseRequestedCapabilities(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ParseBoolean(string? raw)
        => bool.TryParse(raw, out var value) && value;

    private static bool MatchesCapabilities(
        string[] requestedCapabilities,
        SceneDatasetType datasetType)
        => datasetType == SceneDatasetType.HostedTiles &&
           (requestedCapabilities.Length == 0 ||
            requestedCapabilities.All(IsHostedTilesCapability));

    private static bool IsHostedTilesCapability(string capability)
        => string.Equals(capability, ThreeDimensionalTilesCapability, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(capability, HostedTilesDatasetType, StringComparison.OrdinalIgnoreCase);

    private sealed record SceneDiscoveryLookup(SceneDatasetRecord? Record, SceneDataset? Scene);

    private static partial class Log
    {
        [LoggerMessage(EventId = 8416, Level = LogLevel.Error,
            Message = "Public scene discovery operation {Operation} failed.")]
        public static partial void SceneDiscoveryFailed(ILogger logger, string operation, Exception exception);
    }
}
