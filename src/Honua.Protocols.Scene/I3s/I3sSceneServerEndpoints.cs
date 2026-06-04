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

        endpoints.MapGet(
                "/scenes/{sceneId}/SceneServer",
                HandleGetService)
            .WithName("GetI3sSceneService")
            .WithDisplayName("Get I3S Scene Service")
            .WithSummary("Get the Esri I3S SceneServer service descriptor for a hosted scene")
            .WithDescription("Returns the I3S SceneServer service JSON advertising the scene's 3D Object layer for ArcGIS / I3S clients. Enterprise edition.")
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
            .WithDescription("Returns the I3S 3dSceneLayer descriptor (3D Object layer) rooted at the scene extent. Enterprise edition.")
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

        var (extent, minHeight, maxHeight) = await ResolveExtentAsync(context, scene.Id, cancellationToken)
            .ConfigureAwait(false);

        if (layerId is null)
        {
            var service = I3sSceneServiceBuilder.BuildService(scene, extent, minHeight, maxHeight);
            return SerializeService(service);
        }

        var layer = I3sSceneServiceBuilder.BuildLayer(scene, extent, minHeight, maxHeight);
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
    /// Resolves the served layer's horizontal extent (and, when available, its
    /// vertical extent) for the I3S <c>fullExtent</c>.
    /// </summary>
    /// <remarks>
    /// The vertical extent (zmin/zmax) is intentionally <see langword="null"/>:
    /// the persisted <c>SceneDatasetRecord</c> only carries a 2D
    /// <see cref="SceneExtent"/> (XMin/YMin/XMax/YMax) and no min/max height, and
    /// the config-registry path carries no extent at all. The authoritative
    /// vertical bounds live on the per-tile bounding volumes served by the gRPC
    /// <c>TileService</c> (region[4]/region[5]); the I3S descriptor does not have a
    /// height source at this layer, so per OGC 19-008 it advertises a
    /// horizontal-only <c>fullExtent</c> (zmin/zmax omitted) rather than fabricating
    /// a vertical range. If the registration model gains persisted height bounds,
    /// thread them through the trailing tuple slots here.
    /// </remarks>
    private static async Task<(SceneExtent? Extent, double? MinHeight, double? MaxHeight)> ResolveExtentAsync(
        HttpContext context,
        string sceneId,
        CancellationToken cancellationToken)
    {
        var registration = context.RequestServices.GetService<ISceneRegistrationService>();
        if (registration is null)
        {
            return (null, null, null);
        }

        var record = await registration.GetBySceneIdAsync(sceneId, cancellationToken).ConfigureAwait(false);
        return record is null
            ? (null, null, null)
            : (record.Extent, null, null);
    }
}
