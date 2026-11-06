// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
//
// VectorTileServer resources surface for styles, sprites, and glyphs.
// The GeoServices REST default-style document (root.json) is composed from the
// canonical MapLibre style stored for the service's primary layer into a Mapbox GL v8 document
// whose vector source references this service's descriptor (url, #5534) and its
// tile/{z}/{y}/{x}.pbf route (tiles) (VectorTileStyleComposer). The composed style references sprite/glyphs (pointed at the
// scoped-minimal resources/sprites and resources/fonts routes below) ONLY when it has symbol
// layers; otherwise they stay omitted. style sub-resources other than root.json return 404.
//
// Sprites are scoped-minimal (honua-server#1780): resources/sprites/sprite[.json|.png|
// @2x.json|@2x.png] serve an empty sprite index / 1x1 transparent PNG. resources/fonts/
// {fontstack}/{range}.pbf serves the bundled face's glyphs for the requested fontstack and
// range, or not-found for an unserved fontstack or range (#5535) (VectorTileEmbeddedAssets).

using System.Diagnostics;
using Honua.Core.Features.Authorization.Domain;
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

    private static void MapResourcesEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/styles",
                static (HttpContext context, CancellationToken cancellationToken) => HandleGetDefaultStyles(context))
            .WithDisplayName("Get Vector Tile Default Styles")
            .WithName("GetVectorTileDefaultStyles")
            .WithSummary("Get the default vector tile style document (root.json)")
            .WithDescription("Returns the Mapbox GL v8 style JSON for the service, with the vector source referencing this service and its tile template.")
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
            .WithDescription("Returns the root.json style document. Sprite and glyph sub-resources are served under resources/sprites and resources/fonts.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: JsonContentType)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}",
                static (HttpContext context, string spriteResource, CancellationToken cancellationToken)
                    => HandleGetSpriteResource(context, spriteResource))
            .WithDisplayName("Get Vector Tile Sprite Resource")
            .WithName("GetVectorTileSpriteResource")
            .WithSummary("Get a vector tile sprite resource (sprite.json / sprite.png / @2x variants)")
            .WithDescription("Returns the scoped-minimal sprite index (empty JSON object) or sprite image (1x1 transparent PNG). Unknown sprite resources return 404.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: VectorTileEmbeddedAssets.SpriteJsonContentType)
            .Produces(200, contentType: VectorTileEmbeddedAssets.SpritePngContentType)
            .Produces(404);

        endpoints.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf",
                static (HttpContext context, string fontstack, string range, CancellationToken cancellationToken)
                    => HandleGetGlyphRange(context, fontstack, range))
            .WithDisplayName("Get Vector Tile Glyph Range")
            .WithName("GetVectorTileGlyphRange")
            .WithSummary("Get a vector tile glyph range PBF for a fontstack")
            .WithDescription("Returns the glyph PBF for the requested fontstack and range from the bundled sans-serif face. Unserved fontstacks and ranges return 404.")
            .WithTags("VectorTileServer")
            .AllowAnonymous()
            .Produces(200, contentType: VectorTileEmbeddedAssets.GlyphPbfContentType)
            .Produces(404);
    }

    private static Task<IResult> HandleGetDefaultStyles(HttpContext context)
        => ComposeRootStyleAsync(context);

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

            // Select with the tile route's operation so root.json always styles the publication the
            // tiles render (honua-server#4112), even under per-layer operation grants.
            var primary = await ResolvePrimaryVectorTilePublicationAsync(
                snapshot, service, context, AuthorizationOperation.Query, cancellationToken).ConfigureAwait(false);
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
            var spriteUrl = BuildSpriteBaseUrl(context, service.Metadata.Name);
            var glyphsUrl = BuildGlyphsTemplateUrl(context, service.Metadata.Name);
            var serviceUrl = BuildServiceUrl(context, service.Metadata.Name);
            var styleJson = VectorTileStyleComposer.Compose(
                storedMapLibreJson,
                service.Metadata.Name,
                StyleSourceId,
                tileUrl,
                geometryType,
                spriteUrl,
                glyphsUrl,
                serviceUrl);

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

    private static async Task<(MetadataV2Publication Publication, MetadataV2Resource Resource)?> ResolvePrimaryVectorTilePublicationAsync(
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Service service,
        HttpContext context,
        AuthorizationOperation operation,
        CancellationToken cancellationToken)
    {
        (MetadataV2Publication Publication, MetadataV2Resource Resource)? best = null;
        foreach (var publication in TenantScopeHelpers.FilterVisiblePublications(
                     context, snapshot, snapshot.Index.PublicationsByService[service.Metadata.Id]))
        {
            var resource = snapshot.ResolveResource(publication);
            if (!snapshot.IsRoutable(publication))
            {
                continue;
            }

            if (!await AccessPolicyHelpers.IsResourceAccessibleAsync(
                    context, resource!, service, operation, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (best is null
                || IsPreferredPublication(publication, best.Value.Publication))
            {
                best = (publication, resource!);
            }
        }

        return best;
    }

    private static bool IsPreferredPublication(
        MetadataV2Publication candidate,
        MetadataV2Publication current)
    {
        var candidateIsVector = candidate.PublicationType == MetadataV2PublicationType.EsriVectorTileLayer;
        var currentIsVector = current.PublicationType == MetadataV2PublicationType.EsriVectorTileLayer;
        if (candidateIsVector != currentIsVector)
        {
            return candidateIsVector;
        }

        if (candidate.IsPrimary != current.IsPrimary)
        {
            return candidate.IsPrimary;
        }

        var candidateIndex = candidate.LayerIndex ?? int.MaxValue;
        var currentIndex = current.LayerIndex ?? int.MaxValue;
        return candidateIndex < currentIndex;
    }

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

    private static string BuildServiceUrl(HttpContext context, string serviceName)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        var encodedService = Uri.EscapeDataString(serviceName);
        return $"{baseUrl}/rest/services/{encodedService}/VectorTileServer";
    }

    private static string BuildTileTemplateUrl(HttpContext context, string serviceName)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        var encodedService = Uri.EscapeDataString(serviceName);
        return $"{baseUrl}/rest/services/{encodedService}/VectorTileServer/tile/{{z}}/{{y}}/{{x}}.pbf";
    }

    private static string BuildSpriteBaseUrl(HttpContext context, string serviceName)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        var encodedService = Uri.EscapeDataString(serviceName);
        return $"{baseUrl}/rest/services/{encodedService}/VectorTileServer/resources/sprites/sprite";
    }

    private static string BuildGlyphsTemplateUrl(HttpContext context, string serviceName)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        var encodedService = Uri.EscapeDataString(serviceName);
        return $"{baseUrl}/rest/services/{encodedService}/VectorTileServer/resources/fonts/{{fontstack}}/{{range}}.pbf";
    }

    private static async Task<IResult> HandleGetSpriteResource(HttpContext context, string spriteResource)
    {
        var serviceError = await ValidateVectorTileServiceAsync(context);
        if (serviceError is not null)
        {
            return serviceError;
        }

        switch (spriteResource)
        {
            case "sprite.json":
            case "sprite@2x.json":
                return Results.Content(
                    VectorTileEmbeddedAssets.SpriteIndexJson,
                    VectorTileEmbeddedAssets.SpriteJsonContentType);

            case "sprite.png":
            case "sprite@2x.png":
                return Results.Bytes(
                    VectorTileEmbeddedAssets.GetSpritePng(),
                    VectorTileEmbeddedAssets.SpritePngContentType);

            default:
                return StandardErrorHelpers.CreateNotFound(
                    context,
                    $"Sprite resource '{spriteResource}' is not available.");
        }
    }

    // A font stack with no served font, or a range that is not bundled, is not-found rather than an empty glyph stack.
    private static async Task<IResult> HandleGetGlyphRange(HttpContext context, string fontstack, string range)
    {
        var serviceError = await ValidateVectorTileServiceAsync(context);
        if (serviceError is not null)
        {
            return serviceError;
        }

        if (!VectorTileEmbeddedAssets.TryGetGlyphPbf(fontstack, range, out var glyphPbf))
        {
            return StandardErrorHelpers.CreateNotFound(
                context,
                $"Glyph range '{range}' is not available for fontstack '{fontstack}'.");
        }

        return Results.Bytes(glyphPbf, VectorTileEmbeddedAssets.GlyphPbfContentType);
    }

    private static async Task<IResult?> ValidateVectorTileServiceAsync(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            MetadataV2ServiceProtocols.VectorTileServer,
            context,
            cancellationToken: cancellationToken);

        return serviceResult.IsValid ? null : serviceResult.ErrorResult;
    }
}
