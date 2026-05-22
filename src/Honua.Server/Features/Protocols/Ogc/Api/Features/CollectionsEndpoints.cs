// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Collections management endpoints for OGC API Features
/// </summary>
internal static class CollectionsEndpoints
{
    internal const int MaxCollectionProjectionConcurrency = 8;

    private static readonly IReadOnlyDictionary<string, string> _queryablesFormatParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["json"] = MediaTypes.Json,
            ["html"] = MediaTypes.Html,
            ["schemajson"] = MediaTypes.SchemaJson,
            ["schema+json"] = MediaTypes.SchemaJson
        };

    private static readonly string[] _queryablesSupportedMediaTypes =
    [
        MediaTypes.SchemaJson,
        MediaTypes.Json,
        MediaTypes.Html
    ];

    /// <summary>
    /// Maps collections management endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapCollectionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var collections = endpoints.MapGet("/ogc/features/collections", HandleGetCollections)
            .WithDisplayName("OGC API Features Collections")
            .WithName("CollectionInfos")
            .WithSummary("Get OGC API Features collections")
            .WithDescription("Lists all available feature collections")
            .WithTags("OGC API Features")
            .CacheOutput("OgcCollections")
            .Produces<Collections>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        var collection = endpoints.MapGet("/ogc/features/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("OGC API Features Collection")
            .WithName("CollectionInfo")
            .WithSummary("Get OGC API Features collection metadata")
            .WithDescription("Get detailed metadata for a specific collection")
            .WithTags("OGC API Features")
            .CacheOutput("OgcCollection")
            .Produces<CollectionInfo>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        var queryables = endpoints.MapGet("/ogc/features/collections/{collectionId}/queryables", HandleGetQueryables)
            .WithDisplayName("OGC API Features Queryables")
            .WithName("Queryables")
            .WithSummary("Get OGC API Features queryables schema")
            .WithDescription("Get the schema for queryable properties of a collection")
            .WithTags("OGC API Features")
            .CacheOutput("OgcQueryables")
            .Produces<QueryablesSchema>(200, MediaTypes.Json)
            .Produces<QueryablesSchema>(200, MediaTypes.SchemaJson)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles the OGC API Features collections list request
    /// </summary>
    private static async Task<IResult> HandleGetCollections(
        HttpContext context,
        string? f,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICrsRegistry crsRegistry,
        [FromServices] ICoordinateTransformService coordinateTransformService,
        [FromServices] ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        OgcFeaturesLog.CollectionsRequested(logger);

        try
        {
            var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var services = await layerCatalog.ListServicesAsync(cancellationToken);
            var layerToService = LayerValidationHelpers.BuildPrimaryServiceMap(services, ServiceProtocols.OgcFeatures);
            var visibleLayers = layers
                .Where(layer =>
                    layerToService.TryGetValue(layer.Id, out var service)
                        ? ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.OgcFeatures) &&
                            AccessPolicyHelpers.IsLayerAccessible(context, layer, service)
                        : ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.OgcFeatures) &&
                            AccessPolicyHelpers.IsLayerAccessible(context, layer))
                .ToList();
            var collections = await ProjectWithLimitedConcurrencyAsync(
                visibleLayers,
                (layer, ct) => CreateCollectionAsync(
                    layer,
                    layerToService.TryGetValue(layer.Id, out var service) ? service : null,
                    baseUrl,
                    featureReader,
                    crsRegistry,
                    coordinateTransformService,
                    ct),
                cancellationToken).ConfigureAwait(false);

            var links = OgcCommonUtilities.BuildFormatLinks(
                    request,
                    $"{baseUrl}/ogc/features/collections",
                    outputFormat,
                    OgcCommonUtilities.MetadataFormats,
                    "Collections")
                .ToBuilder();

            // Parent (landing page)
            links.Add(Link.Create(
                href: $"{baseUrl}/ogc/features",
                rel: "parent",
                type: MediaTypes.Json,
                title: "Landing page"));

            var response = new Collections
            {
                CollectionList = collections,
                Links = links.ToImmutable()
            };

            OgcFeaturesLog.CollectionsReturned(logger, collections.Length);
            return OgcCommonUtilities.FormatMetadataResponse(response, OgcJsonContext.Default.Collections, outputFormat, "Collections");
        }
        catch (OperationCanceledException)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            // Note: Using static reference to logging from main endpoints class
            CollectionsEndpointLogging.LogInvalidCollectionsRequest(logger, ex);
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid request parameters.");
        }
        catch (InvalidOperationException ex)
        {
            CollectionsEndpointLogging.LogCollectionsQueryFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving collections.");
        }
        catch (Exception ex)
        {
            CollectionsEndpointLogging.LogCollectionsQueryFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving collections.");
        }
    }

    /// <summary>
    /// Handles the OGC API Features single collection request
    /// </summary>
    private static async Task<IResult> HandleGetCollection(
        string collectionId,
        HttpContext context,
        string? f,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICrsRegistry crsRegistry,
        [FromServices] ICoordinateTransformService coordinateTransformService,
        [FromServices] ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);

        try
        {
            var validationError = OgcCommonUtilities.ValidateQueryParameters(request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            OgcFeaturesLog.CollectionRequested(logger, collectionId);

            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var collectionResolution = await ResolveCollectionIdAsync(context, collectionId, cancellationToken);
            if (!collectionResolution.Found)
            {
                if (collectionResolution.ErrorResult != null)
                {
                    return collectionResolution.ErrorResult;
                }

                OgcFeaturesLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            collectionId = collectionResolution.ResolvedCollectionId;

            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                collectionResolution.LayerId,
                LayerValidationHelpers.ValidationProtocol.OgcFeatures,
                requiredProtocol: ServiceProtocols.OgcFeatures,
                cancellationToken: cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }

            var layer = layerValidation.Layer!;
            var services = await layerCatalog.ListServicesAsync(cancellationToken);
            var layerToService = LayerValidationHelpers.BuildPrimaryServiceMap(services, ServiceProtocols.OgcFeatures);
            layerToService.TryGetValue(layer.Id, out var service);

            var collection = await CreateCollectionAsync(layer, service, baseUrl, featureReader, crsRegistry, coordinateTransformService, cancellationToken);
            var collectionSegment = Uri.EscapeDataString(collectionId);
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionSegment}";
            var selfHref = $"{basePath}{request.QueryString}";
            var updatedLinks = collection.Links.Select(link =>
                    string.Equals(link.Rel, RelationTypes.Self, StringComparison.OrdinalIgnoreCase)
                        ? link with { Href = selfHref, Type = outputFormat }
                        : link)
                .ToImmutableArray();

            updatedLinks = OgcCommonUtilities.AddAlternateLinks(updatedLinks, request, basePath, outputFormat, OgcCommonUtilities.MetadataFormats);
            collection = collection with { Links = updatedLinks };

            OgcFeaturesLog.CollectionReturned(logger, collectionId, layer.Name);
            return OgcCommonUtilities.FormatMetadataResponse(
                collection,
                OgcJsonContext.Default.CollectionInfo,
                outputFormat,
                collection.Title ?? collection.Id);
        }
        catch (OperationCanceledException)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the collection.");
        }
        catch (ResourceNotFoundException)
        {
            OgcFeaturesLog.CollectionNotFound(logger, collectionId);
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }
        catch (InvalidOperationException ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the collection.");
        }
        catch (Exception ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the collection.");
        }
    }

    /// <summary>
    /// Handles the OGC API Features queryables request
    /// </summary>
    private static async Task<IResult> HandleGetQueryables(
        string collectionId,
        HttpContext context,
        string? f,
        [FromServices] ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        try
        {
            var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcCommonUtilities.TryGetOutputFormat(
                    f,
                    context,
                    _queryablesFormatParameters,
                    _queryablesSupportedMediaTypes,
                    MediaTypes.Json,
                    out var outputFormat,
                    out var formatError))
            {
                return OgcCommonUtilities.CreateFormatError(context, formatError);
            }

            var effectiveToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var collectionResolution = await ResolveCollectionIdAsync(context, collectionId, effectiveToken);
            if (!collectionResolution.Found)
            {
                if (collectionResolution.ErrorResult != null)
                {
                    return collectionResolution.ErrorResult;
                }

                OgcFeaturesLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            collectionId = collectionResolution.ResolvedCollectionId;
            OgcFeaturesLog.CollectionRequested(logger, collectionId);

            var validation = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
                context,
                collectionId,
                requiredProtocol: ServiceProtocols.OgcFeatures,
                cancellationToken: effectiveToken);
            if (!validation.IsValid)
            {
                return validation.ErrorResult!;
            }

            var resource = validation.Resource!;

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var queryablesId = $"{baseUrl}/ogc/features/collections/{Uri.EscapeDataString(collectionId)}/queryables";

            // Build queryables schema from V2 resource fields
            var queryables = CreateQueryablesSchema(resource, queryablesId);

            return OgcCommonUtilities.FormatMetadataResponse(queryables, OgcJsonContext.Default.QueryablesSchema, outputFormat, "Queryables");
        }
        catch (OperationCanceledException)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the queryables schema.");
        }
        catch (ResourceNotFoundException)
        {
            OgcFeaturesLog.CollectionNotFound(logger, collectionId);
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
        }
        catch (InvalidOperationException ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the queryables schema.");
        }
        catch (Exception ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the queryables schema.");
        }
    }

    /// <summary>
    /// Converts a layer definition to OGC API Features collection
    /// </summary>
    private static async Task<CollectionInfo> CreateCollectionAsync(
        LayerDefinition layer,
        ServiceDefinition? service,
        string baseUrl,
        IFeatureReader featureReader,
        ICrsRegistry crsRegistry,
        ICoordinateTransformService coordinateTransformService,
        CancellationToken cancellationToken)
    {
        // Use layer ID as collection ID (string representation)
        var collectionId = layer.Id.ToString();
        var itemsBaseHref = $"{baseUrl}/ogc/features/collections/{collectionId}/items";
        var collectionLinks = ImmutableArray.CreateBuilder<Link>();

        // Self link
        collectionLinks.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: layer.Name));

        // Items links for all supported encodings
        foreach (var format in OgcFeaturesUtilities.FeatureFormats)
        {
            var href = string.Equals(format.QueryValue, "geojson", StringComparison.OrdinalIgnoreCase)
                ? itemsBaseHref
                : $"{itemsBaseHref}?f={Uri.EscapeDataString(format.QueryValue)}";
            collectionLinks.Add(Link.Create(
                href: href,
                rel: RelationTypes.Items,
                type: format.MediaType,
                title: $"Items ({format.Title})"));
        }

        // Data link (alternate to items)
        collectionLinks.Add(Link.Create(
            href: itemsBaseHref,
            rel: RelationTypes.Data,
            type: MediaTypes.GeoJson,
            title: "Data"));

        var metadata = service?.Metadata ?? layer.Metadata;
        if (ServiceProtocols.IsProtocolEnabled(metadata, ServiceProtocols.OgcApiMaps))
        {
            collectionLinks.Add(Link.Create(
                href: $"{baseUrl}/ogc/maps/collections/{collectionId}/map",
                rel: RelationTypes.Map,
                type: "image/png",
                title: "Map"));
        }

        // Parent (collections)
        collectionLinks.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections",
            rel: "parent",
            type: MediaTypes.Json,
            title: "Collections"));

        // Queryables link
        collectionLinks.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}/queryables",
            rel: RelationTypes.Queryables,
            type: MediaTypes.SchemaJson,
            title: "Queryables"));

        // Style link (MapLibre style JSON)
        collectionLinks.Add(Link.Create(
            href: $"{baseUrl}/api/styles/{layer.Id}.json",
            rel: RelationTypes.Style,
            type: MediaTypes.Json,
            title: "Style"));

        if (ServiceProtocols.IsProtocolEnabled(metadata, ServiceProtocols.OgcApiTiles))
        {
            collectionLinks.Add(Link.Create(
                href: $"{baseUrl}/ogc/tiles/collections/{collectionId}/tiles",
                rel: RelationTypes.TilesetsVector,
                type: MediaTypes.Json,
                title: "Vector tilesets"));
        }

        SpatialExtent? spatialExtent = null;
        if (layer.Extent != null)
        {
            var extentSrid = layer.Extent.Value.SpatialReference;
            (double Lon, double Lat) min = default;
            (double Lon, double Lat) max = default;
            var transformedToCrs84 = false;
            (double Lon, double Lat) minTransformed = default;
            (double Lon, double Lat) maxTransformed = default;
            if (extentSrid != 4326)
            {
                transformedToCrs84 =
                    OgcExtentTransformer.TryTransformToCrs84(layer.Extent.Value.MinX, layer.Extent.Value.MinY, extentSrid, out minTransformed) &&
                    OgcExtentTransformer.TryTransformToCrs84(layer.Extent.Value.MaxX, layer.Extent.Value.MaxY, extentSrid, out maxTransformed);

                // PostGIS fallback for non-WGS84/WebMercator CRS (e.g. NAD83, UTM zones)
                // Uses TransformExtentAsync which transforms all 4 corners to find true min/max
                if (!transformedToCrs84)
                {
                    var extentResult = await coordinateTransformService.TransformExtentAsync(
                        layer.Extent.Value.MinX, layer.Extent.Value.MinY,
                        layer.Extent.Value.MaxX, layer.Extent.Value.MaxY,
                        extentSrid, 4326, cancellationToken);
                    if (extentResult.HasValue)
                    {
                        minTransformed = (extentResult.Value.MinX, extentResult.Value.MinY);
                        maxTransformed = (extentResult.Value.MaxX, extentResult.Value.MaxY);
                        transformedToCrs84 = true;
                    }
                }
            }

            if (extentSrid == 4326 || transformedToCrs84)
            {
                if (extentSrid == 4326)
                {
                    min = (layer.Extent.Value.MinX, layer.Extent.Value.MinY);
                    max = (layer.Extent.Value.MaxX, layer.Extent.Value.MaxY);
                }
                else
                {
                    min = minTransformed;
                    max = maxTransformed;
                }

                spatialExtent = new SpatialExtent
                {
                    BoundingBox = ImmutableArray.Create(ImmutableArray.Create(min.Lon, min.Lat, max.Lon, max.Lat)),
                    Crs = OgcFeaturesUtilities.Crs84Uri
                };
            }
        }

        var temporalExtent = await OgcFeaturesUtilities.BuildTemporalExtentAsync(layer, featureReader, cancellationToken);
        var extent = spatialExtent == null && temporalExtent == null
            ? null
            : new Extent
            {
                Spatial = spatialExtent,
                Temporal = temporalExtent
            };

        var storageCrsDefinition = await crsRegistry.ResolveAsync(
            layer.SpatialReference.ToOgcCrs(),
            cancellationToken);
        var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsUrisAsync(
            layer,
            crsRegistry,
            cancellationToken);

        return new CollectionInfo
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description,
            Links = collectionLinks.ToImmutable(),
            Extent = extent,
            Crs = supportedCrs,
            StorageCrs = storageCrsDefinition?.Uri
        };
    }

    /// <summary>
    /// Creates queryables schema from layer definition
    /// </summary>
    private static QueryablesSchema CreateQueryablesSchema(
        LayerDefinition layer,
        string queryablesId)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, JsonSchemaProperty>();
        var requiredFields = new List<string>();

        // Add properties for all non-geometry fields
        foreach (var field in layer.VisibleAttributeFields.Where(OgcFeaturesUtilities.IsSimpleQueryableField))
        {
            var jsonSchemaProperty = ConvertFieldToJsonSchemaProperty(field);
            properties[field.Name] = jsonSchemaProperty;

            // Add to required array if field is not nullable
            if (!field.Nullable)
            {
                requiredFields.Add(field.Name);
            }
        }

        // Add special geometry property if layer has geometry
        if (layer.HasGeometry && layer.GeometryField != null)
        {
            properties[layer.GeometryField.Name] = new JsonSchemaProperty
            {
                Type = "object",
                Title = "Geometry",
                Description = "Geometric representation of the feature",
                Format = "geometry",
                Ref = "https://geojson.org/schema/Geometry.json"
            };
        }

        return new QueryablesSchema
        {
            Id = queryablesId,
            Type = "object",
            Title = $"Queryables for {layer.Name}",
            Description = $"Schema for queryable properties of the {layer.Name} collection",
            Properties = properties.ToImmutable(),
            Required = requiredFields.ToImmutableArray()
        };
    }

    /// <summary>
    /// Metadata v2 overload of
    /// <see cref="CreateQueryablesSchema(LayerDefinition, string)"/>. Builds the queryables
    /// JSON Schema from <see cref="MetadataV2Resource.SchemaFields"/>, with the primary
    /// geometry field resolved via
    /// <see cref="MetadataV2SpatialExtensions.FindPrimaryGeometryField"/>.
    /// </summary>
    private static QueryablesSchema CreateQueryablesSchema(
        MetadataV2Resource resource,
        string queryablesId)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, JsonSchemaProperty>();
        var requiredFields = new List<string>();
        var geometryField = resource.FindPrimaryGeometryField();
        var geometryFieldName = geometryField?.Name;

        foreach (var field in resource.SchemaFields)
        {
            // Skip the primary geometry field — it gets a dedicated geometry property below.
            if (geometryFieldName is not null &&
                string.Equals(field.Name, geometryFieldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!OgcFeaturesUtilities.IsSimpleQueryableField(field))
            {
                continue;
            }

            properties[field.Name] = ConvertFieldToJsonSchemaProperty(field);

            if (!field.Nullable)
            {
                requiredFields.Add(field.Name);
            }
        }

        if (geometryField is not null)
        {
            properties[geometryField.Name] = new JsonSchemaProperty
            {
                Type = "object",
                Title = "Geometry",
                Description = "Geometric representation of the feature",
                Format = "geometry",
                Ref = "https://geojson.org/schema/Geometry.json"
            };
        }

        var displayName = resource.Metadata.Title ?? resource.Metadata.Name;

        return new QueryablesSchema
        {
            Id = queryablesId,
            Type = "object",
            Title = $"Queryables for {displayName}",
            Description = $"Schema for queryable properties of the {displayName} collection",
            Properties = properties.ToImmutable(),
            Required = requiredFields.ToImmutableArray()
        };
    }

    internal static async Task<ImmutableArray<TProjection>> ProjectWithLimitedConcurrencyAsync<TSource, TProjection>(
        IReadOnlyList<TSource> source,
        Func<TSource, CancellationToken, Task<TProjection>> projector,
        CancellationToken cancellationToken)
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projector);

        if (source.Count == 0)
        {
            return [];
        }

        var results = new TProjection?[source.Count];
        var workerCount = Math.Min(MaxCollectionProjectionConcurrency, source.Count);
        var nextIndex = -1;

        async Task RunWorkerAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var index = Interlocked.Increment(ref nextIndex);
                if (index >= source.Count)
                {
                    return;
                }

                results[index] = await projector(source[index], cancellationToken).ConfigureAwait(false);
            }
        }

        var workers = Enumerable.Range(0, workerCount).Select(_ => RunWorkerAsync());
        await Task.WhenAll(workers).ConfigureAwait(false);
        return results.Select(static result => result!).ToImmutableArray();
    }

    /// <summary>
    /// Converts a FieldDefinition to a JSON Schema property
    /// </summary>
    private static JsonSchemaProperty ConvertFieldToJsonSchemaProperty(FieldDefinition field)
    {
        var (type, format) = GetJsonSchemaTypeAndFormat(field.Type);

        return new JsonSchemaProperty
        {
            Type = type,
            Format = format,
            Title = field.DisplayName ?? field.Name,
            Description = field.Description
        };
    }

    /// <summary>
    /// Metadata v2 overload of <see cref="ConvertFieldToJsonSchemaProperty(FieldDefinition)"/>.
    /// </summary>
    private static JsonSchemaProperty ConvertFieldToJsonSchemaProperty(MetadataV2Field field)
    {
        var (type, format) = GetJsonSchemaTypeAndFormatV2(field.Type);

        return new JsonSchemaProperty
        {
            Type = type,
            Format = format,
            Title = field.Title ?? field.Name,
            Description = field.Description
        };
    }

    /// <summary>
    /// Maps FieldType to JSON Schema type and format
    /// </summary>
    private static (string type, string? format) GetJsonSchemaTypeAndFormat(FieldType fieldType)
        => fieldType switch
        {
            FieldType.String => ("string", null),
            FieldType.Integer => ("integer", null),
            FieldType.BigInteger => ("integer", null),
            FieldType.Double => ("number", "double"),
            FieldType.Float => ("number", "float"),
            FieldType.Boolean => ("boolean", null),
            FieldType.DateTime => ("string", "date-time"),
            FieldType.Date => ("string", "date"),
            FieldType.Time => ("string", "time"),
            FieldType.Uuid => ("string", "uuid"),
            _ => ("string", null)
        };

    /// <summary>
    /// Metadata v2 overload of <see cref="GetJsonSchemaTypeAndFormat(FieldType)"/>.
    /// </summary>
    private static (string type, string? format) GetJsonSchemaTypeAndFormatV2(MetadataV2FieldType type)
        => type switch
        {
            MetadataV2FieldType.String => ("string", null),
            MetadataV2FieldType.Integer => ("integer", null),
            MetadataV2FieldType.BigInteger => ("integer", null),
            MetadataV2FieldType.Double => ("number", "double"),
            MetadataV2FieldType.Float => ("number", "float"),
            MetadataV2FieldType.Boolean => ("boolean", null),
            MetadataV2FieldType.DateTime => ("string", "date-time"),
            MetadataV2FieldType.Date => ("string", "date"),
            MetadataV2FieldType.Time => ("string", "time"),
            MetadataV2FieldType.Uuid => ("string", "uuid"),
            _ => ("string", null)
        };

    private static async Task<CollectionResolution> ResolveCollectionIdAsync(
        HttpContext context,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var routeValidator = context.RequestServices.GetRequiredService<IRouteParameterValidator>();
        var collectionResult = routeValidator.ValidateCollectionId(context);
        if (!collectionResult.IsValid || string.IsNullOrWhiteSpace(collectionResult.Value))
        {
            return new CollectionResolution(
                Found: false,
                ResolvedCollectionId: collectionId,
                LayerId: 0,
                ErrorResult: StandardErrorHelpers.CreateBadRequest(
                    context,
                    collectionResult.ErrorMessage ?? "Collection ID is required."));
        }

        var resolvedCollectionId = collectionResult.Value!;
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await resourceValidator.ValidateCollectionAsync(resolvedCollectionId, cancellationToken);
        if (!validationResult.IsValid)
        {
            var errorResult = validationResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StandardErrorHelpers.CreateBadRequest(context, validationResult.ErrorMessage ?? "Invalid collection ID.")
                : null;
            return new CollectionResolution(
                Found: false,
                ResolvedCollectionId: resolvedCollectionId,
                LayerId: 0,
                ErrorResult: errorResult);
        }

        return new CollectionResolution(
            Found: true,
            ResolvedCollectionId: resolvedCollectionId,
            LayerId: validationResult.Resource!.Id,
            ErrorResult: null);
    }

    private readonly record struct CollectionResolution(
        bool Found,
        string ResolvedCollectionId,
        int LayerId,
        IResult? ErrorResult);

}

/// <summary>
/// Logging helpers for collections endpoints
/// </summary>
internal static partial class CollectionsEndpointLogging
{
    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning,
        Message = "Invalid collections request received")]
    public static partial void LogInvalidCollectionsRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Warning,
        Message = "Invalid collections operation attempted")]
    public static partial void LogInvalidCollectionsOperation(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Error,
        Message = "Collections query failed")]
    public static partial void LogCollectionsQueryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Error,
        Message = "Collection query failed for ID: {CollectionId}")]
    public static partial void LogCollectionQueryFailed(ILogger logger, string collectionId, Exception exception);
}
