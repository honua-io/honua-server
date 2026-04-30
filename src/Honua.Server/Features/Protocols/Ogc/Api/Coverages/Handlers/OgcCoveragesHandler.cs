// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Api.Coverages.Models;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Ogc.Api.Coverages.Handlers;

internal sealed class OgcCoveragesHandler
{
    private const int MaxCollectionProjectionConcurrency = 8;
    private const int MaxScaleSize = 8192;
    private const string CoverageItemType = "coverage";
    private const string GeoTiffContentType = "image/tiff";
    private const string PngContentType = "image/png";
    private const string CoveragesProtocol = ServiceProtocols.OgcApiCoverages;

    private static readonly ImmutableHashSet<string> MetadataQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f");

    private static readonly ImmutableHashSet<string> OpenApiQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f");

    private static readonly ImmutableHashSet<string> CoverageQueryParameters =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "f",
            "bbox",
            "bbox-crs",
            "crs",
            "properties",
            "resolution",
            "scale-factor",
            "scale-size",
            "scale-axes",
            "datetime",
            "subset");

    private static readonly string[] SupportedCoverageMediaTypes = [GeoTiffContentType, PngContentType];

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ICoordinateTransformService _coordinateTransformService;
    private readonly ILogger<OgcCoveragesHandler> _logger;

    public OgcCoveragesHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ICoordinateTransformService coordinateTransformService,
        ILogger<OgcCoveragesHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _coordinateTransformService = coordinateTransformService ?? throw new ArgumentNullException(nameof(coordinateTransformService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IResult GetLandingPage(HttpContext context, string? f)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "landing";
        OgcCoveragesLog.LandingRequested(_logger);

        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                MetadataQueryParameters,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/coverages";
        var links = OgcCoreMetadataUtilities.BuildLandingPageLinks(context, basePath, outputFormat);
        links.Add(Link.Create(
            href: $"{basePath}/openapi.json",
            rel: RelationTypes.ServiceDesc,
            type: MediaTypes.OpenApi,
            title: "API definition"));
        links.Add(Link.Create(
            href: $"{basePath}/conformance",
            rel: RelationTypes.Conformance,
            type: MediaTypes.Json,
            title: "Conformance declaration"));
        links.Add(Link.Create(
            href: $"{basePath}/collections",
            rel: RelationTypes.Data,
            type: MediaTypes.Json,
            title: "Coverage collections"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Coverages",
            Description = "OGC API Coverages implementation for modern raster and coverage access.",
            Supports3d = false,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            landingPage,
            OgcCoveragesJsonContext.Default.LandingPage,
            outputFormat,
            "Landing page");
    }

    public IResult GetConformance(HttpContext context, string? f)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "conformance";
        OgcCoveragesLog.ConformanceRequested(_logger);

        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                MetadataQueryParameters,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var conformancePath = $"{baseUrl}/ogc/coverages/conformance";
        var declaration = new ConformanceDeclaration
        {
            ConformsTo =
            [
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/oas30",
                "http://www.opengis.net/spec/ogcapi-coverages-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-coverages-1/1.0/conf/geodata-coverage",
                "http://www.opengis.net/spec/ogcapi-coverages-1/1.0/conf/geotiff",
                "http://www.opengis.net/spec/ogcapi-coverages-1/1.0/conf/fieldselection",
                "http://www.opengis.net/spec/ogcapi-coverages-1/1.0/conf/crs"
            ],
            Links = OgcCoreMetadataUtilities.BuildConformanceLinks(context, conformancePath, outputFormat)
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            declaration,
            OgcCoveragesJsonContext.Default.ConformanceDeclaration,
            outputFormat,
            "Conformance declaration");
    }

    public Task<IResult> GetOpenApiSpecAsync(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "api";
        OgcCoveragesLog.OpenApiRequested(_logger);

        const string fallbackSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Honua OGC API Coverages",
            "description": "OGC API Coverages implementation for raster coverage access",
            "version": "1.0.0"
          },
          "paths": {}
        }
        """;

        return OgcCoreMetadataUtilities.GetOpenApiSpecAsync(
            context,
            f,
            environment,
            OpenApiQueryParameters,
            "ogc-coverages-openapi.json",
            fallbackSpec);
    }

    public async Task<IResult> GetCollectionsAsync(HttpContext context, string? f, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "collections";
        OgcCoveragesLog.CollectionsRequested(_logger);

        try
        {
            if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                    context,
                    f,
                    MetadataQueryParameters,
                    out var outputFormat,
                    out var errorResult))
            {
                return errorResult!;
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var layers = await _layerCatalog.ListLayersAsync(cancellationToken).ConfigureAwait(false);
            var services = await _layerCatalog.ListServicesAsync(cancellationToken).ConfigureAwait(false);
            var layerToService = LayerValidationHelpers.BuildPrimaryServiceMap(services, CoveragesProtocol);
            var visibleLayers = layers
                .Where(layer =>
                    layerToService.TryGetValue(layer.Id, out var service)
                        ? ServiceProtocols.IsProtocolEnabled(service.Metadata, CoveragesProtocol) &&
                          AccessPolicyHelpers.IsLayerAccessible(context, layer, service)
                        : ServiceProtocols.IsProtocolEnabled(layer.Metadata, CoveragesProtocol) &&
                          AccessPolicyHelpers.IsLayerAccessible(context, layer))
                .OrderBy(static layer => layer.Id)
                .ToArray();

            var projected = await ProjectWithLimitedConcurrencyAsync(
                visibleLayers,
                async (layer, ct) =>
                {
                    var raster = await GetPrimaryRasterWithExtentAsync(layer.Id, ct).ConfigureAwait(false);
                    if (raster is null)
                    {
                        return null;
                    }

                    layerToService.TryGetValue(layer.Id, out var service);
                    return await CreateCollectionAsync(layer, service, raster.Value, baseUrl, ct)
                        .ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            var links = OgcCommonUtilities.BuildFormatLinks(
                    context.Request,
                    $"{baseUrl}/ogc/coverages/collections",
                    outputFormat,
                    OgcCommonUtilities.MetadataFormats,
                    "Coverage collections")
                .ToBuilder();
            links.Add(Link.Create(
                href: $"{baseUrl}/ogc/coverages",
                rel: "parent",
                type: MediaTypes.Json,
                title: "Landing page"));

            var response = new OgcCoverageCollections
            {
                Collections = projected.Where(static collection => collection is not null).Cast<OgcCoverageCollection>().ToImmutableArray(),
                Links = links.ToImmutable()
            };

            return OgcCommonUtilities.FormatMetadataResponse(
                response,
                OgcCoveragesJsonContext.Default.OgcCoverageCollections,
                outputFormat,
                "Coverage collections");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcCoveragesLog.RequestFailed(_logger, ex, "collections", null);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving coverage collections.");
        }
    }

    public async Task<IResult> GetCollectionAsync(
        HttpContext context,
        string collectionId,
        string? f,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "collection";
        OgcCoveragesLog.CollectionRequested(_logger, collectionId);

        try
        {
            if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                    context,
                    f,
                    MetadataQueryParameters,
                    out var outputFormat,
                    out var errorResult))
            {
                return errorResult!;
            }

            var resolution = await ResolveCoverageAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                return resolution.Error;
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var collection = await CreateCollectionAsync(
                    resolution.Layer!,
                    resolution.Service,
                    resolution.Raster!.Value,
                    baseUrl,
                    cancellationToken)
                .ConfigureAwait(false);

            return OgcCommonUtilities.FormatMetadataResponse(
                collection,
                OgcCoveragesJsonContext.Default.OgcCoverageCollection,
                outputFormat,
                "Coverage collection");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcCoveragesLog.RequestFailed(_logger, ex, "collection", collectionId);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving the coverage collection.");
        }
    }

    public async Task<IResult> GetSchemaAsync(
        HttpContext context,
        string collectionId,
        string? f,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "schema";
        OgcCoveragesLog.SchemaRequested(_logger, collectionId);

        try
        {
            if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                    context,
                    f,
                    MetadataQueryParameters,
                    out var outputFormat,
                    out var errorResult))
            {
                return errorResult!;
            }

            var resolution = await ResolveCoverageAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                return resolution.Error;
            }

            var schema = await CreateSchemaAsync(
                    resolution.Layer!,
                    resolution.Raster!.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            return OgcCommonUtilities.FormatMetadataResponse(
                schema,
                OgcCoveragesJsonContext.Default.CoverageSchema,
                outputFormat,
                "Coverage schema",
                jsonContentType: MediaTypes.SchemaJson);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            OgcCoveragesLog.RequestFailed(_logger, ex, "schema", collectionId);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving the coverage schema.");
        }
    }

    public async Task<IResult> GetCoverageAsync(
        HttpContext context,
        string collectionId,
        string? f,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[RequestTelemetryClassifier.OperationItemKey] = "coverage";
        OgcCoveragesLog.CoverageRequested(_logger, collectionId);

        using var telemetry = HonuaTelemetryScope.StartFeature(
            "coverage",
            HonuaTelemetry.Protocols.OgcCoverages,
            collectionId);
        telemetry
            .WithTag(HonuaTelemetry.Tags.Operation, "coverage")
            .WithTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcCoverages);

        try
        {
            var parameterError = ValidateCoverageQueryParameters(context);
            if (parameterError is not null)
            {
                OgcCoveragesLog.ValidationFailed(_logger, collectionId, parameterError);
                return StandardErrorHelpers.CreateBadRequest(context, parameterError);
            }

            var resolution = await ResolveCoverageAsync(context, collectionId, cancellationToken).ConfigureAwait(false);
            if (resolution.Error is not null)
            {
                return resolution.Error;
            }

            if (!TryCreateCoverageQuery(
                    context,
                    f,
                    resolution.Raster!.Value,
                    out var rasterQuery,
                    out var negotiatedFormat,
                    out var outputCrs,
                    out var selectedBandCount,
                    out var queryError,
                    out var notAcceptable))
            {
                OgcCoveragesLog.ValidationFailed(_logger, collectionId, queryError);
                return notAcceptable
                    ? StandardErrorHelpers.CreateNotAcceptable(context, queryError)
                    : StandardErrorHelpers.CreateBadRequest(context, queryError);
            }

            telemetry
                .WithTag(HonuaTelemetry.Tags.LayerId, resolution.Layer!.Id)
                .WithTag("honua.coverage.id", collectionId)
                .WithTag("honua.output.format", negotiatedFormat.ContentType)
                .WithTag("honua.coverage.bbox", rasterQuery.ClipRegion.HasValue)
                .WithTag("honua.coverage.field_count", selectedBandCount ?? resolution.Raster.Value.BandCount);

            var result = await _rasterStore.ExportImageAsync(
                    resolution.Layer.Id,
                    resolution.Raster.Value.Id,
                    rasterQuery,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.Data.Length == 0)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Coverage '{collectionId}' was not found.");
            }

            WriteCoverageHeaders(context, collectionId, negotiatedFormat, outputCrs, result);

            telemetry
                .WithTag("honua.result.bytes", result.Data.Length)
                .WithTag("honua.result.content_type", result.ContentType);
            telemetry.SetSuccess(1);
            OgcCoveragesLog.CoverageReturned(_logger, collectionId, result.Data.Length, result.ContentType);

            return Results.File(result.Data, negotiatedFormat.ContentType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            OgcCoveragesLog.RequestFailed(_logger, ex, "coverage", collectionId);
            telemetry.RecordException(ex);
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid coverage request.");
        }
        catch (Exception ex)
        {
            OgcCoveragesLog.RequestFailed(_logger, ex, "coverage", collectionId);
            telemetry.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while retrieving the coverage.");
        }
    }

    private async Task<CoverageResolution> ResolveCoverageAsync(
        HttpContext context,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessAsync(
                context,
                collectionId,
                requiredProtocol: CoveragesProtocol,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!layerValidation.IsValid)
        {
            return new CoverageResolution(null, null, null, layerValidation.ErrorResult);
        }

        var layer = layerValidation.Layer!;
        var service = await LayerValidationHelpers.ResolvePrimaryServiceAsync(
                context,
                layer.Id,
                CoveragesProtocol,
                cancellationToken)
            .ConfigureAwait(false);
        var raster = await GetPrimaryRasterWithExtentAsync(layer.Id, cancellationToken).ConfigureAwait(false);
        if (raster is null)
        {
            return new CoverageResolution(
                null,
                null,
                null,
                StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' does not expose a raster coverage."));
        }

        return new CoverageResolution(layer, service, raster.Value, null);
    }

    private async Task<RasterInfo?> GetPrimaryRasterWithExtentAsync(int layerId, CancellationToken cancellationToken)
    {
        var raster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (raster is null)
        {
            return null;
        }

        if (raster.Value.Extent is null)
        {
            var extent = await _rasterStore.GetExtentAsync(layerId, raster.Value.Id, cancellationToken).ConfigureAwait(false);
            if (extent.HasValue)
            {
                raster = raster.Value with { Extent = extent };
            }
        }

        return raster;
    }

    private async Task<OgcCoverageCollection> CreateCollectionAsync(
        LayerDefinition layer,
        ServiceDefinition? service,
        RasterInfo raster,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
        var escapedCollectionId = Uri.EscapeDataString(collectionId);
        var basePath = $"{baseUrl}/ogc/coverages/collections/{escapedCollectionId}";
        var links = ImmutableArray.CreateBuilder<Link>();
        links.Add(Link.Create(
            href: basePath,
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: layer.Name));
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/coverages/collections",
            rel: "parent",
            type: MediaTypes.Json,
            title: "Coverage collections"));
        links.Add(Link.Create(
            href: $"{basePath}/schema",
            rel: RelationTypes.Schema,
            type: MediaTypes.SchemaJson,
            title: "Coverage schema"));
        links.Add(Link.Create(
            href: $"{basePath}/coverage",
            rel: RelationTypes.Coverage,
            type: GeoTiffContentType,
            title: "Coverage data"));
        links.Add(Link.Create(
            href: $"{basePath}/coverage?f=png",
            rel: RelationTypes.Alternate,
            type: PngContentType,
            title: "Coverage data as PNG"));

        if (service is not null)
        {
            links.Add(Link.Create(
                href: $"{baseUrl}/rest/services/{Uri.EscapeDataString(service.Name)}/ImageServer",
                rel: RelationTypes.DescribedBy,
                type: MediaTypes.Json,
                title: "GeoServices ImageServer metadata"));
        }

        var storageSrid = ResolveStorageSrid(layer, raster);
        var storageCrs = CreateEpsgUri(storageSrid);
        var crs = ImmutableArray.Create(SpatialReferenceHelpers.Crs84Uri, storageCrs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var storageBbox = CreateStorageBoundingBox(raster);
        var extent = await CreateExtentAsync(raster, storageSrid, storageBbox, cancellationToken).ConfigureAwait(false);

        return new OgcCoverageCollection
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description,
            ItemType = CoverageItemType,
            Extent = extent,
            Links = links.ToImmutable(),
            Crs = crs,
            StorageCrs = storageCrs,
            Grid = CreateGrid(raster),
            Domain = CreateDomain(raster),
            DefaultFields = CreateDefaultFields(raster)
        };
    }

    private async Task<Extent?> CreateExtentAsync(
        RasterInfo raster,
        int storageSrid,
        ImmutableArray<ImmutableArray<double>>? storageBbox,
        CancellationToken cancellationToken)
    {
        if (raster.Extent is not { } rasterExtent)
        {
            return null;
        }

        var crs84Extent = await TransformExtentToCrs84Async(rasterExtent, storageSrid, cancellationToken).ConfigureAwait(false);
        if (!crs84Extent.HasValue)
        {
            return new Extent
            {
                Spatial = new SpatialExtent
                {
                    BoundingBox = ImmutableArray.Create(ImmutableArray.Create(
                        rasterExtent.XMin,
                        rasterExtent.YMin,
                        rasterExtent.XMax,
                        rasterExtent.YMax)),
                    StorageCrsBoundingBox = storageBbox,
                    Crs = CreateEpsgUri(storageSrid)
                }
            };
        }

        var (minLon, minLat, maxLon, maxLat) = crs84Extent.Value;
        return new Extent
        {
            Spatial = new SpatialExtent
            {
                BoundingBox = ImmutableArray.Create(ImmutableArray.Create(minLon, minLat, maxLon, maxLat)),
                StorageCrsBoundingBox = storageBbox,
                Crs = SpatialReferenceHelpers.Crs84Uri
            }
        };
    }

    private static ImmutableArray<ImmutableArray<double>>? CreateStorageBoundingBox(RasterInfo raster)
    {
        return raster.Extent is { } extent
            ? ImmutableArray.Create(ImmutableArray.Create(extent.XMin, extent.YMin, extent.XMax, extent.YMax))
            : null;
    }

    private async Task<(double MinLon, double MinLat, double MaxLon, double MaxLat)?> TransformExtentToCrs84Async(
        RasterExtent extent,
        int storageSrid,
        CancellationToken cancellationToken)
    {
        if (storageSrid == SpatialReference.WGS84.Wkid)
        {
            return (extent.XMin, extent.YMin, extent.XMax, extent.YMax);
        }

        var transformed = await _coordinateTransformService.TransformExtentAsync(
                extent.XMin,
                extent.YMin,
                extent.XMax,
                extent.YMax,
                storageSrid,
                SpatialReference.WGS84.Wkid,
                cancellationToken)
            .ConfigureAwait(false);

        return transformed.HasValue
            ? (transformed.Value.MinX, transformed.Value.MinY, transformed.Value.MaxX, transformed.Value.MaxY)
            : null;
    }

    private static CoverageGrid CreateGrid(RasterInfo raster)
    {
        var (origin, resolution) = ResolveGridTransform(raster);
        return new CoverageGrid
        {
            AxisLabels = ["x", "y"],
            Width = raster.Width,
            Height = raster.Height,
            Origin = origin,
            Resolution = resolution
        };
    }

    private static CoverageDomain? CreateDomain(RasterInfo raster)
    {
        if (raster.Extent is not { } extent)
        {
            return null;
        }

        var (_, resolution) = ResolveGridTransform(raster);
        double? xResolution = resolution.HasValue && resolution.Value.Length > 0 ? Math.Abs(resolution.Value[0]) : null;
        double? yResolution = resolution.HasValue && resolution.Value.Length > 1 ? Math.Abs(resolution.Value[1]) : null;

        return new CoverageDomain
        {
            Axes = ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new[]
                {
                    new KeyValuePair<string, CoverageAxis>("x", new CoverageAxis
                    {
                        Start = extent.XMin,
                        Stop = extent.XMax,
                        Count = raster.Width,
                        Resolution = xResolution
                    }),
                    new KeyValuePair<string, CoverageAxis>("y", new CoverageAxis
                    {
                        Start = extent.YMin,
                        Stop = extent.YMax,
                        Count = raster.Height,
                        Resolution = yResolution
                    })
                })
        };
    }

    private static (ImmutableArray<double>? Origin, ImmutableArray<double>? Resolution) ResolveGridTransform(RasterInfo raster)
    {
        if (raster.GeoTransform is { Length: >= 6 } transform)
        {
            return (
                ImmutableArray.Create(transform[0], transform[3]),
                ImmutableArray.Create(transform[1], transform[5]));
        }

        if (raster.Extent is { } extent && raster.Width > 0 && raster.Height > 0)
        {
            return (
                ImmutableArray.Create(extent.XMin, extent.YMax),
                ImmutableArray.Create(
                    (extent.XMax - extent.XMin) / raster.Width,
                    (extent.YMin - extent.YMax) / raster.Height));
        }

        return (null, null);
    }

    private async Task<CoverageSchema> CreateSchemaAsync(
        LayerDefinition layer,
        RasterInfo raster,
        CancellationToken cancellationToken)
    {
        RasterStatistics[] statistics = [];
        try
        {
            statistics = await _rasterStore.GetStatisticsAsync(layer.Id, raster.Id, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            OgcCoveragesLog.RequestFailed(_logger, ex, "schema-statistics", layer.Id.ToString(CultureInfo.InvariantCulture));
        }

        var statisticsByBand = statistics.ToDictionary(static stat => stat.Band);
        var properties = ImmutableDictionary.CreateBuilder<string, CoverageSchemaProperty>(StringComparer.Ordinal);
        for (var band = 1; band <= Math.Max(raster.BandCount, 1); band++)
        {
            statisticsByBand.TryGetValue(band, out var bandStatistics);
            properties[CreateBandName(band)] = new CoverageSchemaProperty
            {
                Type = IsIntegerPixelType(raster.PixelType) ? "integer" : "number",
                Title = "Band " + band.ToString(CultureInfo.InvariantCulture),
                Description = "Coverage raster band " + band.ToString(CultureInfo.InvariantCulture),
                Minimum = bandStatistics.MinValue,
                Maximum = bandStatistics.MaxValue,
                PropertySequence = band,
                PixelType = raster.PixelType,
                NoDataValue = raster.NoDataValue
            };
        }

        return new CoverageSchema
        {
            Title = "Coverage schema for " + layer.Name,
            Description = "Field selection schema for OGC API Coverages collection " + layer.Id.ToString(CultureInfo.InvariantCulture),
            Properties = properties.ToImmutable()
        };
    }

    private static string? ValidateCoverageQueryParameters(HttpContext context)
    {
        foreach (var parameter in context.Request.Query.Keys)
        {
            if (!CoverageQueryParameters.Contains(parameter))
            {
                return $"Unsupported coverage parameter '{parameter}'.";
            }
        }

        if (context.Request.Query.ContainsKey("datetime"))
        {
            return "The datetime parameter is not supported by this OGC API Coverages implementation.";
        }

        if (context.Request.Query.ContainsKey("subset"))
        {
            return "The subset parameter is not supported by this OGC API Coverages implementation. Use bbox for spatial subsetting.";
        }

        if (context.Request.Query.ContainsKey("scale-axes"))
        {
            return "The scale-axes parameter is not supported by this OGC API Coverages implementation. Use resolution, scale-factor, or scale-size.";
        }

        return null;
    }

    private static bool TryCreateCoverageQuery(
        HttpContext context,
        string? f,
        RasterInfo raster,
        out RasterQuery rasterQuery,
        out CoverageFormat negotiatedFormat,
        out CrsDefinition? outputCrs,
        out int? selectedBandCount,
        out string error,
        out bool notAcceptable)
    {
        rasterQuery = default;
        negotiatedFormat = default;
        outputCrs = null;
        selectedBandCount = null;
        error = string.Empty;
        notAcceptable = false;

        if (!TryResolveCoverageFormat(f, context, out negotiatedFormat, out error, out notAcceptable))
        {
            return false;
        }

        var query = new RasterQuery { OutputFormat = negotiatedFormat.Format };

        if (!TryApplyBbox(context, ref query, out error))
        {
            return false;
        }

        if (!TryApplyOutputCrs(context, ref query, out outputCrs, out error))
        {
            return false;
        }

        if (!TryApplyProperties(context, raster, ref query, out selectedBandCount, out error))
        {
            return false;
        }

        if (!TryApplyScaling(context, raster, ref query, out error))
        {
            return false;
        }

        rasterQuery = query;
        return true;
    }

    private static bool TryResolveCoverageFormat(
        string? f,
        HttpContext context,
        out CoverageFormat format,
        out string error,
        out bool notAcceptable)
    {
        format = default;
        error = string.Empty;
        notAcceptable = false;

        if (!string.IsNullOrWhiteSpace(f))
        {
            return TryResolveFormatParameter(f, out format, out error);
        }

        var acceptHeader = context.Request.Headers.Accept;
        if (!StringValues.IsNullOrEmpty(acceptHeader))
        {
            var acceptRanges = ContentNegotiationHelpers.ParseAcceptHeader(acceptHeader);
            if (acceptRanges.IsDefaultOrEmpty ||
                !ContentNegotiationHelpers.TrySelectBestMediaType(SupportedCoverageMediaTypes, acceptHeader, out var selectedMediaType))
            {
                error = "Requested coverage format is not acceptable.";
                notAcceptable = true;
                return false;
            }

            format = string.Equals(selectedMediaType, PngContentType, StringComparison.OrdinalIgnoreCase)
                ? new CoverageFormat(RasterFormat.PNG, PngContentType, "png")
                : new CoverageFormat(RasterFormat.TIFF, GeoTiffContentType, "geotiff");
            return true;
        }

        format = new CoverageFormat(RasterFormat.TIFF, GeoTiffContentType, "geotiff");
        return true;
    }

    private static bool TryResolveFormatParameter(string f, out CoverageFormat format, out string error)
    {
        format = default;
        error = string.Empty;
        switch (f.Trim().ToLowerInvariant())
        {
            case "geotiff":
            case "tiff":
            case "tif":
            case GeoTiffContentType:
                format = new CoverageFormat(RasterFormat.TIFF, GeoTiffContentType, "geotiff");
                return true;
            case "png":
            case PngContentType:
                format = new CoverageFormat(RasterFormat.PNG, PngContentType, "png");
                return true;
            case "netcdf":
            case "application/netcdf":
                error = "NetCDF coverage encoding is not supported by this OGC API Coverages implementation.";
                return false;
            case "jpeg":
            case "jpg":
            case "image/jpeg":
                error = "JPEG is not supported for OGC API Coverages coverage payloads. Use GeoTIFF or PNG.";
                return false;
            default:
                error = $"Unsupported coverage format '{f}'. Use geotiff, tiff, tif, png, image/tiff, or image/png.";
                return false;
        }
    }

    private static bool TryApplyBbox(HttpContext context, ref RasterQuery query, out string error)
    {
        error = string.Empty;
        var bbox = OgcCommonUtilities.GetQueryValue(context.Request, "bbox");
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return true;
        }

        var bboxCrsValue = OgcCommonUtilities.GetQueryValue(context.Request, "bbox-crs") ?? SpatialReferenceHelpers.Crs84Uri;
        if (!SpatialReferenceHelpers.TryParseCrsDefinition(bboxCrsValue, out var bboxCrs))
        {
            error = "bbox-crs must be a supported CRS identifier.";
            return false;
        }

        if (!RasterParsingHelpers.TryParseBoundingBox(
                bbox,
                bboxCrs.AxisOrder,
                bboxCrs.IsGeographic,
                out var minX,
                out var minY,
                out var maxX,
                out var maxY))
        {
            error = "bbox must contain xmin,ymin,xmax,ymax coordinates in the declared bbox-crs.";
            return false;
        }

        query = query with { ClipRegion = CreateClipRegion(new Envelope(minX, maxX, minY, maxY), bboxCrs.Srid) };
        return true;
    }

    private static bool TryApplyOutputCrs(
        HttpContext context,
        ref RasterQuery query,
        out CrsDefinition? outputCrs,
        out string error)
    {
        outputCrs = null;
        error = string.Empty;
        var crsValue = OgcCommonUtilities.GetQueryValue(context.Request, "crs");
        if (string.IsNullOrWhiteSpace(crsValue))
        {
            return true;
        }

        if (!SpatialReferenceHelpers.TryParseCrsDefinition(crsValue, out var crs))
        {
            error = "crs must be a supported CRS identifier.";
            return false;
        }

        outputCrs = crs;
        query = query with { OutputSrid = crs.Srid };
        return true;
    }

    private static bool TryApplyProperties(
        HttpContext context,
        RasterInfo raster,
        ref RasterQuery query,
        out int? selectedBandCount,
        out string error)
    {
        selectedBandCount = null;
        error = string.Empty;
        var properties = OgcCommonUtilities.GetQueryValue(context.Request, "properties");
        if (string.IsNullOrWhiteSpace(properties))
        {
            return true;
        }

        var tokens = properties.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length != properties.Split(',').Length)
        {
            error = "properties must contain one or more comma-separated band names.";
            return false;
        }

        var bands = new int[tokens.Length];
        var seen = new HashSet<int>();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!TryParseBandName(tokens[i], out var band))
            {
                error = $"Unsupported coverage property '{tokens[i]}'. Use band_1 through band_{raster.BandCount.ToString(CultureInfo.InvariantCulture)}.";
                return false;
            }

            if (band < 1 || band > Math.Max(raster.BandCount, 1))
            {
                error = $"Coverage property '{tokens[i]}' is outside the available band range.";
                return false;
            }

            if (!seen.Add(band))
            {
                error = $"Coverage property '{tokens[i]}' was requested more than once.";
                return false;
            }

            bands[i] = band;
        }

        query = query with { Bands = bands };
        selectedBandCount = bands.Length;
        return true;
    }

    private static bool TryApplyScaling(HttpContext context, RasterInfo raster, ref RasterQuery query, out string error)
    {
        error = string.Empty;
        var scalingParameters = 0;
        if (context.Request.Query.ContainsKey("resolution"))
        {
            scalingParameters++;
        }

        if (context.Request.Query.ContainsKey("scale-factor"))
        {
            scalingParameters++;
        }

        if (context.Request.Query.ContainsKey("scale-size"))
        {
            scalingParameters++;
        }

        if (scalingParameters > 1)
        {
            error = "Use only one of resolution, scale-factor, or scale-size.";
            return false;
        }

        var resolution = OgcCommonUtilities.GetQueryValue(context.Request, "resolution");
        if (!string.IsNullOrWhiteSpace(resolution))
        {
            if (!TryParseResolution(resolution, out var pixelWidth, out var pixelHeight))
            {
                error = "resolution must be a positive number or two positive comma-separated numbers.";
                return false;
            }

            var requestedPixelSize = new PixelSize { Width = pixelWidth, Height = pixelHeight };
            if (!TryValidateDerivedScaleSize(context, raster, requestedPixelSize, out error))
            {
                return false;
            }

            query = query with { PixelSize = requestedPixelSize };
            return true;
        }

        var scaleFactor = OgcCommonUtilities.GetQueryValue(context.Request, "scale-factor");
        if (!string.IsNullOrWhiteSpace(scaleFactor))
        {
            if (!double.TryParse(scaleFactor, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) ||
                !double.IsFinite(factor) ||
                factor <= 0)
            {
                error = "scale-factor must be a positive number.";
                return false;
            }

            var nativePixelSize = ResolveNativePixelSize(raster);
            if (!nativePixelSize.HasValue)
            {
                error = "scale-factor requires coverage pixel size metadata.";
                return false;
            }

            var requestedPixelSize = new PixelSize
            {
                Width = nativePixelSize.Value.Width * factor,
                Height = nativePixelSize.Value.Height * factor
            };
            if (!TryValidateDerivedScaleSize(context, raster, requestedPixelSize, out error))
            {
                return false;
            }

            query = query with { PixelSize = requestedPixelSize };
            return true;
        }

        var scaleSize = OgcCommonUtilities.GetQueryValue(context.Request, "scale-size");
        if (!string.IsNullOrWhiteSpace(scaleSize))
        {
            if (!TryParseScaleSize(scaleSize, out var width, out var height))
            {
                error = $"scale-size must be width,height or axis size pairs such as x(512),y(512), with values from 1 to {MaxScaleSize.ToString(CultureInfo.InvariantCulture)}.";
                return false;
            }

            query = query with { OutputWidth = width, OutputHeight = height };
        }

        return true;
    }

    private static bool TryParseResolution(string value, out double pixelWidth, out double pixelHeight)
    {
        pixelWidth = pixelHeight = 0;
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (1 or 2))
        {
            return false;
        }

        if (!TryParsePositiveDouble(parts[0], out pixelWidth))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            pixelHeight = pixelWidth;
            return true;
        }

        return TryParsePositiveDouble(parts[1], out pixelHeight);
    }

    private static bool TryParseScaleSize(string value, out int width, out int height)
    {
        width = height = 0;
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (TryParsePositiveInt(parts[0], out width) && TryParsePositiveInt(parts[1], out height))
        {
            return true;
        }

        return TryParseAxisSize(parts[0], isX: true, out width) &&
               TryParseAxisSize(parts[1], isX: false, out height);
    }

    private static bool TryParseAxisSize(string value, bool isX, out int size)
    {
        size = 0;
        var open = value.IndexOf('(', StringComparison.Ordinal);
        var close = value.LastIndexOf(')');
        if (open <= 0 || close != value.Length - 1 || close <= open + 1)
        {
            return false;
        }

        var axis = value[..open].Trim();
        var expectedAxis = isX
            ? axis is "x" or "X" or "lon" or "Lon" or "long" or "Long"
            : axis is "y" or "Y" or "lat" or "Lat";
        return expectedAxis && TryParsePositiveInt(value[(open + 1)..close], out size);
    }

    private static bool TryParsePositiveDouble(string value, out double parsed)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
               double.IsFinite(parsed) &&
               parsed > 0;
    }

    private static bool TryParsePositiveInt(string value, out int parsed)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
               parsed is > 0 and <= MaxScaleSize;
    }

    private static bool TryValidateDerivedScaleSize(
        HttpContext context,
        RasterInfo raster,
        PixelSize pixelSize,
        out string error)
    {
        error = string.Empty;
        if (!IsFinitePositive(pixelSize.Width) || !IsFinitePositive(pixelSize.Height))
        {
            error = "resolution and scale-factor must resolve to positive finite pixel sizes.";
            return false;
        }

        if (!TryResolveRequestedScaleExtentSize(context, raster, out var extentWidth, out var extentHeight))
        {
            error = "resolution and scale-factor require coverage extent or pixel size metadata.";
            return false;
        }

        var outputWidth = Math.Ceiling(extentWidth / pixelSize.Width);
        var outputHeight = Math.Ceiling(extentHeight / pixelSize.Height);
        if (!IsFinitePositive(outputWidth) ||
            !IsFinitePositive(outputHeight) ||
            outputWidth > MaxScaleSize ||
            outputHeight > MaxScaleSize)
        {
            error = $"resolution and scale-factor must not request more than {MaxScaleSize.ToString(CultureInfo.InvariantCulture)} pixels on either axis. Use a coarser resolution, a larger scale-factor, or scale-size.";
            return false;
        }

        return true;
    }

    private static bool TryResolveRequestedScaleExtentSize(
        HttpContext context,
        RasterInfo raster,
        out double extentWidth,
        out double extentHeight)
    {
        extentWidth = 0;
        extentHeight = 0;

        var bbox = OgcCommonUtilities.GetQueryValue(context.Request, "bbox");
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var bboxCrsValue = OgcCommonUtilities.GetQueryValue(context.Request, "bbox-crs") ?? SpatialReferenceHelpers.Crs84Uri;
            if (!SpatialReferenceHelpers.TryParseCrsDefinition(bboxCrsValue, out var bboxCrs) ||
                !RasterParsingHelpers.TryParseBoundingBox(
                    bbox,
                    bboxCrs.AxisOrder,
                    bboxCrs.IsGeographic,
                    out var minX,
                    out var minY,
                    out var maxX,
                    out var maxY))
            {
                return false;
            }

            extentWidth = maxX - minX;
            extentHeight = maxY - minY;
            return IsFinitePositive(extentWidth) && IsFinitePositive(extentHeight);
        }

        if (raster.Extent is { } extent)
        {
            extentWidth = Math.Abs(extent.XMax - extent.XMin);
            extentHeight = Math.Abs(extent.YMax - extent.YMin);
            if (IsFinitePositive(extentWidth) && IsFinitePositive(extentHeight))
            {
                return true;
            }
        }

        var nativePixelSize = ResolveNativePixelSize(raster);
        if (nativePixelSize.HasValue && raster.Width > 0 && raster.Height > 0)
        {
            extentWidth = nativePixelSize.Value.Width * raster.Width;
            extentHeight = nativePixelSize.Value.Height * raster.Height;
            return IsFinitePositive(extentWidth) && IsFinitePositive(extentHeight);
        }

        return false;
    }

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private static PixelSize? ResolveNativePixelSize(RasterInfo raster)
    {
        var (_, resolution) = ResolveGridTransform(raster);
        if (resolution.HasValue && resolution.Value.Length >= 2)
        {
            return new PixelSize
            {
                Width = Math.Abs(resolution.Value[0]),
                Height = Math.Abs(resolution.Value[1])
            };
        }

        return null;
    }

    private static RasterClipRegion CreateClipRegion(Envelope envelope, int srid)
    {
        var factory = new GeometryFactory();
        var geometry = factory.ToGeometry(envelope);
        var writer = new WKBWriter();
        return new RasterClipRegion
        {
            Geometry = writer.Write(geometry),
            Srid = srid
        };
    }

    private static void WriteCoverageHeaders(
        HttpContext context,
        string collectionId,
        CoverageFormat format,
        CrsDefinition? outputCrs,
        RasterResult result)
    {
        if (result.Extent is { } extent)
        {
            context.Response.Headers.Append(
                "Content-Bbox",
                string.Join(
                    ",",
                    FormatDouble(extent.XMin),
                    FormatDouble(extent.YMin),
                    FormatDouble(extent.XMax),
                    FormatDouble(extent.YMax)));
        }

        var resultSrid = outputCrs?.Srid ?? result.Srid;
        if (resultSrid.HasValue && resultSrid.Value != SpatialReference.WGS84.Wkid)
        {
            context.Response.Headers.Append("Content-Crs", FormatContentCrsHeader(resultSrid.Value));
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var collectionSegment = Uri.EscapeDataString(collectionId);
        var basePath = $"{baseUrl}/ogc/coverages/collections/{collectionSegment}/coverage";
        context.Response.Headers.Append(
            "Link",
            $"<{basePath}{context.Request.QueryString}>; rel=\"self\"; type=\"{format.ContentType}\", <{basePath}?f=geotiff>; rel=\"alternate\"; type=\"{GeoTiffContentType}\", <{basePath}?f=png>; rel=\"alternate\"; type=\"{PngContentType}\"");
    }

    private static ImmutableArray<string> CreateDefaultFields(RasterInfo raster)
        => Enumerable.Range(1, Math.Max(raster.BandCount, 1))
            .Select(CreateBandName)
            .ToImmutableArray();

    private static bool TryParseBandName(string value, out int band)
    {
        band = 0;
        const string prefix = "band_";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(value[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out band);
    }

    private static string CreateBandName(int band)
        => "band_" + band.ToString(CultureInfo.InvariantCulture);

    private static string CreateEpsgUri(int srid)
        => FormattableString.Invariant($"http://www.opengis.net/def/crs/EPSG/0/{srid}");

    private static string FormatContentCrsHeader(int srid)
        => FormattableString.Invariant($"<https://www.opengis.net/def/crs/EPSG/0/{srid}>");

    private static int ResolveStorageSrid(LayerDefinition layer, RasterInfo raster)
        => raster.Extent?.Srid ?? raster.Srid ?? layer.SpatialReference.Wkid;

    private static string FormatDouble(double value)
        => value.ToString("G17", CultureInfo.InvariantCulture);

    private static bool IsIntegerPixelType(string pixelType)
        => pixelType.Contains("BUI", StringComparison.OrdinalIgnoreCase) ||
           pixelType.Contains("BSI", StringComparison.OrdinalIgnoreCase);

    private static async Task<ImmutableArray<TProjection?>> ProjectWithLimitedConcurrencyAsync<TSource, TProjection>(
        TSource[] source,
        Func<TSource, CancellationToken, Task<TProjection?>> projector,
        CancellationToken cancellationToken)
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projector);

        if (source.Length == 0)
        {
            return [];
        }

        var results = new TProjection?[source.Length];
        var workerCount = Math.Min(MaxCollectionProjectionConcurrency, source.Length);
        var nextIndex = -1;

        async Task RunWorkerAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = Interlocked.Increment(ref nextIndex);
                if (index >= source.Length)
                {
                    return;
                }

                results[index] = await projector(source[index], cancellationToken).ConfigureAwait(false);
            }
        }

        var workers = Enumerable.Range(0, workerCount).Select(_ => RunWorkerAsync());
        await Task.WhenAll(workers).ConfigureAwait(false);
        return results.ToImmutableArray();
    }

    private readonly record struct CoverageFormat(RasterFormat Format, string ContentType, string QueryValue);

    private readonly record struct CoverageResolution(
        LayerDefinition? Layer,
        ServiceDefinition? Service,
        RasterInfo? Raster,
        IResult? Error);
}
