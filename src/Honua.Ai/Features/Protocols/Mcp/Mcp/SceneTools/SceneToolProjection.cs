// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.SceneTools;

internal static class SceneToolProjection
{
    public static McpSceneSummary ToSummary(SceneCatalogEntry entry, SceneCatalogResolution resolution)
        => new()
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            ServingFormat = SceneServingProjection.ToWireValue(entry.ServingFormat),
            ContentKind = SceneServingProjection.ToWireValue(entry.ContentKind),
            TilesetUrl = resolution.TilesetUrl,
            Bounds = ToBounds(entry.Extent),
            Capabilities = resolution.Capabilities,
            Auth = ToAuth(resolution.RequiresAuthentication, resolution.AuthSchemes),
            UpdatedAt = entry.UpdatedAt
        };

    public static McpSceneMetadataOutput ToMetadata(
        SceneCatalogEntry entry,
        SceneCatalogResolution resolution,
        IReadOnlyList<SceneCatalogLink> links)
    {
        var bounds = ToBounds(entry.Extent);
        var tileset = resolution.Endpoints
            .FirstOrDefault(e => string.Equals(
                e.EndpointFormat,
                SceneServingProjection.ThreeDTilesWireFormat,
                StringComparison.Ordinal));

        return new McpSceneMetadataOutput
        {
            Id = entry.Id,
            Name = entry.Name,
            Description = entry.Description,
            ServingFormat = SceneServingProjection.ToWireValue(entry.ServingFormat),
            ContentKind = SceneServingProjection.ToWireValue(entry.ContentKind),
            Tileset = tileset is null ? null : ToEndpoint(tileset),
            Center = bounds is null
                ? null
                : new McpSceneCoordinate
                {
                    Latitude = (bounds.South + bounds.North) / 2,
                    Longitude = (bounds.West + bounds.East) / 2,
                    Height = 1200
                },
            Bounds = bounds,
            Capabilities = resolution.Capabilities,
            Auth = ToAuth(resolution.RequiresAuthentication, resolution.AuthSchemes),
            Links = links
                .Select(static link => new McpSceneLink
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

    public static McpSceneResolveOutput ToResolveOutput(
        SceneCatalogResolution resolution,
        IReadOnlyList<SceneCatalogEndpoint> endpoints)
        => new()
        {
            SceneId = resolution.SceneId,
            ServingFormat = SceneServingProjection.ToWireValue(resolution.ServingFormat),
            ContentKind = SceneServingProjection.ToWireValue(resolution.ContentKind),
            TilesetUrl = resolution.TilesetUrl,
            Endpoints = endpoints.Select(ToEndpoint).ToArray(),
            Capabilities = resolution.Capabilities,
            Auth = ToAuth(resolution.RequiresAuthentication, resolution.AuthSchemes)
        };

    public static void EnsureReadable(HttpContext httpContext, SceneCatalogEntry entry)
    {
        if (entry.AccessPolicy is not { } policy)
        {
            return;
        }

        var decision = SceneReadAccess.Evaluate(httpContext, policy);
        if (!decision.IsAllowed)
        {
            throw new GeoprocessingAuthorizationException(
                decision.FailureReason ?? $"Caller is not authorized to read scene '{entry.Id}'.");
        }
    }

    public static string ResolveBaseUrl(HttpContext httpContext)
        => BaseUrlResolver.GetBaseUrl(httpContext);

    private static McpSceneEndpoint ToEndpoint(SceneCatalogEndpoint endpoint)
        => new()
        {
            Kind = endpoint.Kind,
            EndpointFormat = endpoint.EndpointFormat,
            Url = endpoint.Url,
            MediaType = endpoint.MediaType,
            RequiresAuthentication = endpoint.RequiresAuthentication,
            AccessSession = endpoint.AccessSession is { } session
                ? new McpSceneAccessSessionAffordance { Href = session.Href, Method = session.Method }
                : null
        };

    private static McpSceneAuthRequirements ToAuth(bool requiresAuthentication, IReadOnlyList<string> schemes)
        => new()
        {
            RequiresAuthentication = requiresAuthentication,
            Schemes = schemes
        };

    private static McpSceneBounds? ToBounds(SceneExtent? extent)
        => extent is null
            ? null
            : new McpSceneBounds
            {
                West = extent.XMin,
                South = extent.YMin,
                East = extent.XMax,
                North = extent.YMax
            };
}
