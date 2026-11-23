// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Catalog;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Scene;
using Honua.Protocols.Scene.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Scene;

internal static partial class SceneDiscoveryEndpoints
{
    private const string ScenesTag = "Scenes";
    private const string HostedTilesLegacyCapability = "hosted_tiles";
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
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status500InternalServerError);

        MapSceneDetails(endpoints);
        return endpoints;
    }

    private static void MapSceneDetails(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/scenes/{sceneId}/resolve", HandleResolveScene)
            .WithName("ResolvePublicScene")
            .WithDisplayName("Resolve Public Scene Render Endpoints")
            .WithSummary("Resolve a hosted scene into render-ready endpoint URLs")
            .WithDescription("Returns SDK-compatible render endpoint metadata for a hosted scene.")
            .WithTags(ScenesTag)
            .Produces<PublicSceneResolution>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status500InternalServerError);

        endpoints.MapGet("/api/scenes/{sceneId}", HandleGetScene)
            .WithName("GetPublicScene")
            .WithDisplayName("Get Public Scene Discovery Metadata")
            .WithSummary("Get SDK-compatible metadata for a hosted scene")
            .WithDescription("Returns public/mobile discovery metadata for a hosted scene without exposing admin management fields.")
            .WithTags(ScenesTag)
            .Produces<PublicSceneMetadata>(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status500InternalServerError);
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
            if (includeInactive && await RequireAdministratorAsync(context).ConfigureAwait(false) is { } inactiveDenied)
            {
                return inactiveDenied;
            }

            if (!TryParseListFilters(context.Request.Query, out var filters, out var filterError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, filterError);
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var registration = context.RequestServices.GetService<ISceneRegistrationService>();
            var entries = await EnumerateCatalogEntriesAsync(
                context,
                registration,
                registry,
                options.Value ?? new SceneDatasetOptions(),
                includeInactive,
                cancellationToken).ConfigureAwait(false);

            var summaries = entries
                .Where(entry => MatchesFilters(entry, filters))
                .Select(entry => ToSummary(entry, baseUrl))
                .OrderBy(scene => scene.Id, StringComparer.Ordinal)
                .ToArray();

            var response = new PublicSceneListResponse { Scenes = summaries };
            return Results.Json(response, PublicSceneDiscoveryJsonContext.Default.PublicSceneListResponse);
        }
        // Endpoint boundary: catch-all is intentional here, translated to a logged 500 below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
            var (scene, denied) = await ResolveReadableSceneAsync(sceneId, context, registry, cancellationToken)
                .ConfigureAwait(false);
            if (denied is not null)
            {
                return denied;
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var response = ToMetadata(SceneCatalog.ToEntry(scene!.Value), baseUrl);
            return Results.Json(response, PublicSceneDiscoveryJsonContext.Default.PublicSceneMetadata);
        }
        // Endpoint boundary: catch-all is intentional here, translated to a logged 500 below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
            var (scene, denied) = await ResolveReadableSceneAsync(sceneId, context, registry, cancellationToken)
                .ConfigureAwait(false);
            if (denied is not null)
            {
                return denied;
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var response = ToResolution(SceneCatalog.ToEntry(scene!.Value), baseUrl);
            return Results.Json(response, PublicSceneDiscoveryJsonContext.Default.PublicSceneResolution);
        }
        // Endpoint boundary: catch-all is intentional here, translated to a logged 500 below.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.SceneDiscoveryFailed(logger, "resolve", ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to resolve scene.");
        }
    }

    private static async Task<List<SceneCatalogEntry>> EnumerateCatalogEntriesAsync(
        HttpContext context,
        ISceneRegistrationService? registration,
        ISceneDatasetRegistry registry,
        SceneDatasetOptions options,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var entries = new List<SceneCatalogEntry>();
        if (includeInactive)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (registration is not null)
            {
                var records = await registration.ListAsync(includeInactive: true, cancellationToken).ConfigureAwait(false);
                foreach (var record in records)
                {
                    seen.Add(record.Id);
                    entries.Add(SceneCatalog.ToEntry(record));
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
                    entries.Add(SceneCatalog.ToEntry(scene));
                }
            }

            return entries;
        }

        var catalog = await SceneCatalog
            .ListActiveAsync(registration, registry, options, cancellationToken)
            .ConfigureAwait(false);

        foreach (var resolved in catalog.Where(scene => SceneReadAccess.IsReadable(context, scene.AccessPolicy)))
        {
            entries.Add(SceneCatalog.ToEntry(resolved));
        }

        return entries;
    }

    private static async Task<(ResolvedScene? Scene, IResult? Denied)> ResolveReadableSceneAsync(
        string sceneId,
        HttpContext context,
        ISceneDatasetRegistry registry,
        CancellationToken cancellationToken)
    {
        var registration = context.RequestServices.GetService<ISceneRegistrationService>();
        var scene = await SceneCatalog.FindActiveAsync(sceneId, registration, registry, cancellationToken)
            .ConfigureAwait(false);
        return scene is { } resolved
            ? (resolved, RequireReadAccess(context, resolved))
            : (null, StandardErrorHelpers.CreateNotFound(context, "Scene was not found."));
    }

    private static IResult? RequireReadAccess(HttpContext context, ResolvedScene scene)
        => scene.AccessPolicy is { } accessPolicy
            ? AccessPolicyHelpers.CreateAccessDeniedResult(context, SceneReadAccess.Evaluate(context, accessPolicy))
            : null;

    private static async Task<IResult?> RequireAdministratorAsync(HttpContext context)
    {
        if (await OperationAdminAuthorization
                .IsAuthorizedAsync(context, context.User, OperationSideEffectClass.ReadOnly, context.RequestAborted)
                .ConfigureAwait(false))
        {
            return null;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return StandardErrorHelpers.CreateForbidden(context, "Listing inactive scenes requires an administrator.");
        }

        AccessPolicyHelpers.AppendAuthenticationChallenge(context);
        return StandardErrorHelpers.CreateUnauthorized(context, AccessPolicyHelpers.AuthRequiredMessage);
    }

    internal static PublicSceneSummary ToSummary(SceneCatalogEntry entry, string baseUrl)
    {
        var resolution = SceneCatalog.ResolveEndpoints(entry, baseUrl);
        return new PublicSceneSummary
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            ServingFormat = SceneServingProjection.ToWireValue(entry.ServingFormat),
            ContentKind = SceneServingProjection.ToWireValue(entry.ContentKind),
            TilesetUrl = resolution.TilesetUrl,
            Bounds = ToBounds(entry.Extent),
            Capabilities = resolution.Capabilities.ToArray(),
            Auth = ToAuth(entry.RequiresAuthentication),
            UpdatedAt = entry.UpdatedAt
        };
    }

    internal static PublicSceneMetadata ToMetadata(SceneCatalogEntry entry, string baseUrl)
    {
        var resolution = SceneCatalog.ResolveEndpoints(entry, baseUrl);
        var bounds = ToBounds(entry.Extent);
        var tilesetEndpoint = resolution.Endpoints
            .FirstOrDefault(endpoint => string.Equals(
                endpoint.EndpointFormat,
                SceneServingProjection.ThreeDTilesWireFormat,
                StringComparison.Ordinal));

        return new PublicSceneMetadata
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            ServingFormat = SceneServingProjection.ToWireValue(entry.ServingFormat),
            ContentKind = SceneServingProjection.ToWireValue(entry.ContentKind),
            Tileset = tilesetEndpoint is null ? null : ToEndpointDto(tilesetEndpoint),
            Center = ToCenter(bounds),
            Bounds = bounds,
            Capabilities = resolution.Capabilities.ToArray(),
            Auth = ToAuth(entry.RequiresAuthentication),
            Links = SceneCatalog.BuildLinks(entry, baseUrl)
                .Select(static link => new PublicSceneLink
                {
                    Rel = link.Rel,
                    Href = link.Href,
                    Type = link.Type,
                    Title = link.Title
                })
                .ToArray(),
            UpdatedAt = entry.UpdatedAt
        };
    }

    internal static PublicSceneResolution ToResolution(SceneCatalogEntry entry, string baseUrl)
    {
        var resolution = SceneCatalog.ResolveEndpoints(entry, baseUrl);
        return new PublicSceneResolution
        {
            SceneId = resolution.SceneId,
            ServingFormat = SceneServingProjection.ToWireValue(resolution.ServingFormat),
            ContentKind = SceneServingProjection.ToWireValue(resolution.ContentKind),
            TilesetUrl = resolution.TilesetUrl,
            Capabilities = resolution.Capabilities.ToArray(),
            Auth = ToAuth(resolution.RequiresAuthentication),
            Endpoints = resolution.Endpoints.Select(ToEndpointDto).ToArray()
        };
    }

    internal static PublicSceneAuthRequirements ToAuth(SceneDatasetRecord record)
        => ToAuth(SceneServingProjection.ToAccessPolicy(record) is not null);

    private static PublicSceneAuthRequirements ToAuth(bool requiresAuthentication)
        => new()
        {
            RequiresAuthentication = requiresAuthentication,
            Schemes = requiresAuthentication ? ProtectedSchemes : []
        };

    private static PublicSceneEndpoint ToEndpointDto(SceneCatalogEndpoint endpoint)
        => new()
        {
            Kind = endpoint.Kind,
            EndpointFormat = endpoint.EndpointFormat,
            Url = endpoint.Url,
            Format = endpoint.EndpointFormat,
            MediaType = endpoint.MediaType,
            RequiresAuthentication = endpoint.RequiresAuthentication,
            AccessSession = endpoint.AccessSession is { } session
                ? new PublicSceneAccessSessionAffordance { Href = session.Href, Method = session.Method }
                : null
        };

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

    private static bool TryParseListFilters(
        IQueryCollection query,
        out SceneDiscoveryListFilters filters,
        out string error)
    {
        filters = default;
        error = string.Empty;

        SceneServingFormat? servingFormat = null;
        var rawFormat = query["servingFormat"].ToString();
        if (!string.IsNullOrWhiteSpace(rawFormat))
        {
            if (!SceneServingProjection.TryParseServingFormat(rawFormat, out var parsedFormat))
            {
                error = $"Unknown servingFormat '{rawFormat}'.";
                return false;
            }

            servingFormat = parsedFormat;
        }

        SceneContentKind? contentKind = null;
        var rawKind = query["contentKind"].ToString();
        if (!string.IsNullOrWhiteSpace(rawKind))
        {
            if (!SceneServingProjection.TryParseContentKind(rawKind, out var parsedKind))
            {
                error = $"Unknown contentKind '{rawKind}'.";
                return false;
            }

            contentKind = parsedKind;
        }

        var capabilities = ParseRequestedCapabilities(query["capabilities"].ToString());
        filters = new SceneDiscoveryListFilters(capabilities, servingFormat, contentKind);
        return true;
    }

    private static bool MatchesFilters(SceneCatalogEntry entry, SceneDiscoveryListFilters filters)
    {
        if (filters.ServingFormat is { } expectedFormat && entry.ServingFormat != expectedFormat)
        {
            return false;
        }

        if (filters.ContentKind is { } expectedKind && entry.ContentKind != expectedKind)
        {
            return false;
        }

        if (filters.RequestedCapabilities.Length == 0)
        {
            return true;
        }

        return filters.RequestedCapabilities.All(capability => MatchesCapability(entry.ServingFormat, capability));
    }

    private static bool MatchesCapability(SceneServingFormat servingFormat, string capability)
    {
        if (servingFormat == SceneServingFormat.ThreeDTiles
            && string.Equals(capability, HostedTilesLegacyCapability, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SceneServingProjection.TryParseServingFormat(capability, out var parsed)
            && parsed == servingFormat;
    }

    private static string[] ParseRequestedCapabilities(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ParseBoolean(string? raw)
        => bool.TryParse(raw, out var value) && value;

    private readonly record struct SceneDiscoveryListFilters(
        string[] RequestedCapabilities,
        SceneServingFormat? ServingFormat,
        SceneContentKind? ContentKind);

    private static partial class Log
    {
        [LoggerMessage(EventId = 8416, Level = LogLevel.Error,
            Message = "Public scene discovery operation {Operation} failed.")]
        public static partial void SceneDiscoveryFailed(ILogger logger, string operation, Exception exception);
    }
}
