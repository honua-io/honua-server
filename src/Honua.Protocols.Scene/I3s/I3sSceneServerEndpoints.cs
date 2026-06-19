// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Scene.Assets;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Scene.I3s;

/// <summary>
/// Read-only Esri I3S <c>SceneServer</c> REST endpoints that present a hosted
/// Honua scene to ArcGIS / I3S clients as a 3D Object scene layer (#1202).
/// </summary>
/// <remarks>
/// <para>
/// The surface mirrors the I3S REST binding: a service root, the scene-layer
/// descriptor at <c>/layers/0</c>, and a placeholder node-page resource. It is
/// Enterprise-gated — I3S serving is an enterprise migration/parity feature.
/// </para>
/// <para>
/// This slice serves the service and layer descriptor JSON so a client can
/// discover the layer; per-node geometry/attribute/texture binary streaming is
/// a tracked follow-up.
/// </para>
/// </remarks>
internal static partial class I3sSceneServerEndpoints
{
    private const string ScenesTag = "Scenes";
    private const string I3sContentType = "application/json";

    public static IEndpointRouteBuilder MapI3sSceneServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Canonical GeoServices REST path (#1806): a registered Enterprise scene
        // is discoverable as an Esri SceneServer under /rest/services/{id}, the
        // same addressing every other GeoServices service type uses, so the
        // catalog (#1807) and ArcGIS clients resolve it without a bespoke route.
        endpoints.MapGet(
                "/rest/services/{sceneId}/SceneServer",
                HandleGetService)
            .WithName("GetGeoServicesSceneService")
            .WithDisplayName("Get GeoServices SceneServer Service")
            .WithSummary("Get the Esri I3S SceneServer service descriptor at the GeoServices path")
            .WithDescription("Returns the I3S SceneServer service JSON advertising the scene's 3D Object layer for ArcGIS / I3S clients at the canonical /rest/services GeoServices path. Enterprise edition.")
            .WithTags(ScenesTag)
            .Produces<I3sSceneServiceDocument>(StatusCodes.Status200OK, contentType: I3sContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        endpoints.MapGet(
                "/rest/services/{sceneId}/SceneServer/layers/{layerId:int}",
                HandleGetLayer)
            .WithName("GetGeoServicesSceneLayer")
            .WithDisplayName("Get GeoServices SceneServer Layer")
            .WithSummary("Get the Esri I3S scene-layer descriptor at the GeoServices path")
            .WithDescription("Returns the I3S 3dSceneLayer descriptor (3D Object layer) rooted at the scene extent at the canonical /rest/services GeoServices path. Enterprise edition.")
            .WithTags(ScenesTag)
            .Produces<I3sSceneLayerDocument>(StatusCodes.Status200OK, contentType: I3sContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        // Legacy /scenes/{id}/SceneServer routes (#1202) retained as a
        // documented ALIAS of the GeoServices path above so existing clients and
        // links keep working. Both paths share the same handlers and descriptor.
        endpoints.MapGet(
                "/scenes/{sceneId}/SceneServer",
                HandleGetService)
            .WithName("GetI3sSceneService")
            .WithDisplayName("Get I3S Scene Service")
            .WithSummary("Get the Esri I3S SceneServer service descriptor for a hosted scene")
            .WithDescription("Alias of /rest/services/{sceneId}/SceneServer. Returns the I3S SceneServer service JSON advertising the scene's 3D Object layer for ArcGIS / I3S clients. Enterprise edition.")
            .WithTags(ScenesTag)
            .Produces<I3sSceneServiceDocument>(StatusCodes.Status200OK, contentType: I3sContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        endpoints.MapGet(
                "/scenes/{sceneId}/SceneServer/layers/{layerId:int}",
                HandleGetLayer)
            .WithName("GetI3sSceneLayer")
            .WithDisplayName("Get I3S Scene Layer")
            .WithSummary("Get the Esri I3S scene-layer descriptor for a hosted scene")
            .WithDescription("Alias of /rest/services/{sceneId}/SceneServer/layers/{layerId}. Returns the I3S 3dSceneLayer descriptor (3D Object layer) rooted at the scene extent. Enterprise edition.")
            .WithTags(ScenesTag)
            .Produces<I3sSceneLayerDocument>(StatusCodes.Status200OK, contentType: I3sContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static Task<IResult> HandleGetService(
        string sceneId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] ILicenseStatusProvider licenseStatusProvider,
        CancellationToken cancellationToken)
        => HandleAsync(sceneId, layerId: null, context, registry, licenseStatusProvider, cancellationToken);

    private static Task<IResult> HandleGetLayer(
        string sceneId,
        int layerId,
        HttpContext context,
        [FromServices] ISceneDatasetRegistry registry,
        [FromServices] ILicenseStatusProvider licenseStatusProvider,
        CancellationToken cancellationToken)
        => HandleAsync(sceneId, layerId, context, registry, licenseStatusProvider, cancellationToken);

    private static async Task<IResult> HandleAsync(
        string sceneId,
        int? layerId,
        HttpContext context,
        ISceneDatasetRegistry registry,
        ILicenseStatusProvider licenseStatusProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Scene identifier is required.");
        }

        var edition = licenseStatusProvider.GetCurrentStatus().Edition;
        if (edition < HonuaEdition.Enterprise)
        {
            return StandardErrorHelpers.CreateForbidden(
                context,
                $"I3S scene serving requires the Enterprise edition. Current edition: {edition}.");
        }

        // Only layer 0 exists per scene; reject any other index explicitly so a
        // client probing layer ids gets a deterministic 404 rather than the
        // layer-0 body.
        if (layerId is { } requestedLayerId && requestedLayerId != I3sSceneServiceBuilder.LayerId)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Scene layer was not found.");
        }

        var scene = await registry.FindAsync(sceneId, cancellationToken).ConfigureAwait(false);
        if (scene is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Scene was not found.");
        }

        if (scene.AccessPolicy is { } accessPolicy)
        {
            var deniedResult = AccessPolicyHelpers.RequireAccess(
                context,
                layerPolicy: accessPolicy,
                servicePolicy: null,
                scope: AccessScope.Read);
            if (deniedResult is not null)
            {
                return deniedResult;
            }
        }

        var extent = await ResolveExtentAsync(context, scene, cancellationToken)
            .ConfigureAwait(false);

        if (layerId is null)
        {
            var service = I3sSceneServiceBuilder.BuildService(scene, extent);
            return SerializeService(service);
        }

        var layer = I3sSceneServiceBuilder.BuildLayer(scene, extent);
        return SerializeLayer(layer);
    }

    private static IResult SerializeService(I3sSceneServiceDocument service)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            service,
            I3sServingJsonContext.Default.I3sSceneServiceDocument);
        return Results.Bytes(bytes, I3sContentType);
    }

    private static IResult SerializeLayer(I3sSceneLayerDocument layer)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            layer,
            I3sServingJsonContext.Default.I3sSceneLayerDocument);
        return Results.Bytes(bytes, I3sContentType);
    }

    /// <summary>
    /// Resolves the served layer's extent for the I3S <c>fullExtent</c>: the
    /// persisted horizontal envelope plus, when the tileset is loadable, the
    /// vertical (z) range read from the tileset's root bounding volume.
    /// </summary>
    /// <remarks>
    /// The horizontal envelope comes from the persisted
    /// <see cref="SceneDatasetRecord"/> (a 2D <see cref="SceneExtent"/>); the
    /// config-registry path carries no record and so no horizontal extent. The
    /// vertical bounds are read honestly from the 3D Tiles root
    /// <c>boundingVolume.region</c> (region[4]/region[5], min/max height in
    /// metres) — the same authoritative source the gRPC <c>TileService</c> uses.
    /// When neither a persisted extent nor a loadable tileset z-range is
    /// available the vertical extent is omitted (per OGC 19-008) rather than
    /// fabricating a 0..0 range.
    /// </remarks>
    private static async Task<SceneExtent?> ResolveExtentAsync(
        HttpContext context,
        SceneDataset scene,
        CancellationToken cancellationToken)
    {
        SceneExtent? horizontal = null;
        var registration = context.RequestServices.GetService<ISceneRegistrationService>();
        if (registration is not null)
        {
            var record = await registration.GetBySceneIdAsync(scene.Id, cancellationToken).ConfigureAwait(false);
            horizontal = record?.Extent;
        }

        var verticalRange = TryReadTilesetVerticalRange(scene);

        if (horizontal is null)
        {
            // No persisted horizontal extent: a vertical range alone is not a
            // usable fullExtent, so omit the extent entirely.
            return null;
        }

        if (verticalRange is not { } range)
        {
            return horizontal;
        }

        return horizontal with { ZMin = range.MinHeight, ZMax = range.MaxHeight };
    }

    /// <summary>
    /// Reads the scene tileset's root <c>boundingVolume.region</c> min/max
    /// height (region[4]/region[5], metres) when the <c>tileset.json</c> is
    /// loadable under the scene's asset root. Returns <see langword="null"/>
    /// when the tileset is absent, unparsable, or carries no 6-element region —
    /// so the descriptor advertises a vertical range only when it is honestly
    /// known.
    /// </summary>
    private static (double MinHeight, double MaxHeight)? TryReadTilesetVerticalRange(SceneDataset scene)
    {
        if (!SceneAssetResolver.TryResolve(scene, scene.TilesetFileName, out var resolved, out _))
        {
            return null;
        }

        try
        {
            using var stream = resolved.File.OpenRead();
            var document = JsonSerializer.Deserialize(
                stream,
                TilesetJsonContext.Default.TilesetDocument);

            var region = document?.Root.BoundingVolume.Region;
            if (region is not { Length: >= 6 })
            {
                return null;
            }

            return (region[4], region[5]);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing/locked/corrupt tileset is not fatal to descriptor
            // serving: fall back to a horizontal-only fullExtent rather than
            // failing the request.
            return null;
        }
    }
}
