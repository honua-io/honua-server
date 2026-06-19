// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// VectorTileServer resources/styles surface (honua-server#1779, epic #1776). Implements the
// Esri-compatible default-style document (root.json) by composing the canonical MapLibre style
// stored for the service's primary layer into a Mapbox GL v8 document whose vector source is
// pointed at this service's tile/{z}/{y}/{x}.pbf route (VectorTileStyleComposer). Per the epic
// decision, sprite and glyphs references are OMITTED until the sprite/glyph pipeline lands
// (honua-server#1780); style sub-resources other than root.json return 404.

using System.Diagnostics;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.VectorTileServer.Services;
using Honua.ServiceDefaults;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.GeoServices.VectorTileServer;

internal static partial class VectorTileServerEndpoints
{
    // The single vector source id emitted in the composed style. Esri vector tile styles
    // conventionally use "esri"; clients bind layers to this source.
    private const string StyleSourceId = "esri";

    /// <summary>
    /// Maps the VectorTileServer resources routes (default styles, sprites, glyphs). The
    /// service descriptor advertises <c>resources/styles</c> as <c>defaultStyles</c>.
    /// Implemented by honua-server#1779; <c>resources/styles</c> and
    /// <c>resources/styles/root.json</c> return the composed GL style, other sub-resources 404.
    /// </summary>
    private static void MapResourcesEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/styles",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetDefaultStyles(context))
            .WithDisplayName("Get Vector Tile Default Styles")
            .WithName("GetVectorTileDefaultStyles")
            .WithSummary("Get the default vector tile style document (root.json)")
            .WithDescription("Returns the Mapbox GL v8 style JSON for the service, with the vector source pointed at this service's tile template.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: JsonContentType)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}",
                static (HttpContext context, string resourcePath, CancellationToken cancellationToken)
                    => HandleGetStyleResource(context, resourcePath))
            .WithDisplayName("Get Vector Tile Style Resource")
            .WithName("GetVectorTileStyleResource")
            .WithSummary("Get a vector tile style sub-resource (root.json)")
            .WithDescription("Returns the root.json style document. Sprite and glyph sub-resources are not served yet (honua-server#1780) and return 404.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: JsonContentType)
            .Produces(404);
    }

    /// <summary>
    /// Serves the default style document at <c>resources/styles</c>.
    /// </summary>
    private static Task<IResult> HandleGetDefaultStyles(HttpContext context)
        => ComposeRootStyleAsync(context);

    /// <summary>
    /// Serves style sub-resources. Only <c>root.json</c> (and the bare path) is served; sprite
    /// and glyph sub-resources are not produced yet (honua-server#1780) and return 404.
    /// </summary>
    private static Task<IResult> HandleGetStyleResource(HttpContext context, string resourcePath)
    {
        var normalized = (resourcePath ?? string.Empty).Trim('/');
        if (normalized.Length == 0
            || string.Equals(normalized, "root.json", StringComparison.OrdinalIgnoreCase))
        {
            return ComposeRootStyleAsync(context);
        }

        return Task.FromResult(StandardErrorHelpers.CreateNotFound(
            context,
            $"Style resource '{resourcePath}' is not available."));
    }

    /// <summary>
    /// Resolves the service and its primary vector tile publication, composes the Mapbox GL v8
    /// style document (canonical MapLibre style rewritten onto this service's tile route, or a
    /// deterministic default when the layer has no stored style), and returns it as JSON.
    /// </summary>
    private static async Task<IResult> ComposeRootStyleAsync(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.VectorTileServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "get-default-styles");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                MetadataV2ServiceProtocols.VectorTileServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            var primary = ResolvePrimaryStylePublication(snapshot, service, context);
            if (primary is null)
            {
                return StandardErrorHelpers.CreateNotFound(
                    context,
                    "The VectorTileServer service has no accessible vector tile layer.");
            }

            var (_, resource) = primary.Value;
            var styleId = resource.Metadata.Name;
            var geometryType = resource.ReadGeometryType();

            var storedMapLibreJson = await ResolveStoredMapLibreStyleAsync(
                context,
                styleId,
                cancellationToken).ConfigureAwait(false);

            var tileUrl = BuildTileTemplateUrl(context, service.Metadata.Name);
            var styleJson = VectorTileStyleComposer.Compose(
                storedMapLibreJson,
                service.Metadata.Name,
                StyleSourceId,
                tileUrl,
                geometryType);

            stopwatch.Stop();
            scope.SetSuccess(1);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            return Results.Content(styleJson, JsonContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }

    /// <summary>
    /// Resolves the primary, access-permitted vector tile publication for the service (preferring
    /// the publication flagged <see cref="MetadataV2Publication.IsPrimary"/>, then the lowest
    /// layer index) together with its backing resource.
    /// </summary>
    private static (MetadataV2Publication Publication, MetadataV2Resource Resource)? ResolvePrimaryStylePublication(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        HttpContext context)
    {
        (MetadataV2Publication Publication, MetadataV2Resource Resource)? best = null;
        foreach (var publication in snapshot.Index.PublicationsByService[service.Metadata.Id])
        {
            var resource = snapshot.ResolveResource(publication);
            if (resource is null)
            {
                continue;
            }

            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
            {
                continue;
            }

            if (best is null
                || IsPreferredPublication(publication, best.Value.Publication))
            {
                best = (publication, resource);
            }
        }

        return best;
    }

    private static bool IsPreferredPublication(
        MetadataV2Publication candidate,
        MetadataV2Publication current)
    {
        if (candidate.IsPrimary != current.IsPrimary)
        {
            return candidate.IsPrimary;
        }

        var candidateIndex = candidate.LayerIndex ?? int.MaxValue;
        var currentIndex = current.LayerIndex ?? int.MaxValue;
        return candidateIndex < currentIndex;
    }

    /// <summary>
    /// Reads the canonical MapLibre style stored for the service's primary layer via the shared
    /// OGC style projection. Returns <see langword="null"/> when no style is stored so the
    /// composer falls back to a deterministic default.
    /// </summary>
    private static async Task<string?> ResolveStoredMapLibreStyleAsync(
        HttpContext context,
        string styleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(styleId))
        {
            return null;
        }

        var projection = context.RequestServices.GetService<IOgcStyleProjection>();
        if (projection is null)
        {
            return null;
        }

        var stylesheet = await projection
            .GetStylesheetAsync(styleId, OgcStyleEncoding.MapboxStyle, cancellationToken)
            .ConfigureAwait(false);

        return stylesheet?.Content;
    }

    /// <summary>
    /// Builds the absolute tile template for the service, for example
    /// <c>https://host/rest/services/{name}/VectorTileServer/tile/{z}/{y}/{x}.pbf</c>.
    /// </summary>
    private static string BuildTileTemplateUrl(HttpContext context, string serviceName)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        var encodedService = Uri.EscapeDataString(serviceName);
        return $"{baseUrl}/rest/services/{encodedService}/VectorTileServer/tile/{{z}}/{{y}}/{{x}}.pbf";
    }
}
