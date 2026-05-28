// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
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
    private const string OgcFeaturesProtocolName = "OgcFeatures";
    private const string OgcApiMapsProtocolName = "OGC-API-Maps";
    private const string OgcApiTilesProtocolName = "OGC-API-Tiles";

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
        [FromServices] IMetadataV2GraphProvider graphProvider,
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
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            // Walk OGC API Features publications: each is a (resource, service) pair gated
            // on protocol enablement + access policy. Enforce the canonical-service
            // boundary: a resource only appears here through its IsPrimary publication —
            // showing a layer only because the caller has access to a secondary, non-
            // canonical publication leaks the canonical boundary (see
            // OgcServiceBoundaryTests.GetCollections_WithSharedLayerInSecondaryService_DoesNotLeakCanonicalBoundary).
            // First pass: discover the canonical (IsPrimary) OgcFeatures publication per resource.
            var canonicalByResource = new Dictionary<string, (MetadataV2Publication Publication, MetadataV2Service Service)>(StringComparer.OrdinalIgnoreCase);
            foreach (var publication in snapshot.Graph.Publications)
            {
                if (!publication.IsPrimary)
                {
                    continue;
                }
                if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service))
                {
                    continue;
                }
                if (!IsProtocolEnabled(service, OgcFeaturesProtocolName))
                {
                    continue;
                }
                var resource = snapshot.ResolveResource(publication);
                if (resource is null)
                {
                    continue;
                }
                canonicalByResource[resource.Metadata.Id] = (publication, service);
            }

            var publicationsByResource = new Dictionary<string, (MetadataV2Publication Publication, MetadataV2Service Service, MetadataV2Resource Resource)>(StringComparer.OrdinalIgnoreCase);
            foreach (var publication in snapshot.Graph.Publications)
            {
                if (!snapshot.Index.ServicesById.TryGetValue(publication.ServiceId, out var service))
                {
                    continue;
                }
                if (!IsProtocolEnabled(service, OgcFeaturesProtocolName))
                {
                    continue;
                }
                var resource = snapshot.ResolveResource(publication);
                if (resource is null)
                {
                    continue;
                }
                if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, service))
                {
                    continue;
                }

                // Canonical-boundary enforcement: when a resource has an IsPrimary
                // publication, only surface the layer if the caller can read THAT
                // canonical publication. Otherwise hide it (the resource may also be
                // exposed via a secondary service the caller has access to, but
                // surfacing it there would leak across canonical boundaries).
                if (canonicalByResource.TryGetValue(resource.Metadata.Id, out var canonical))
                {
                    if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, canonical.Service))
                    {
                        continue;
                    }
                    if (!publicationsByResource.ContainsKey(resource.Metadata.Id))
                    {
                        publicationsByResource[resource.Metadata.Id] = (canonical.Publication, canonical.Service, resource);
                    }
                    continue;
                }

                // No canonical publication exists for this resource — first match wins.
                if (!publicationsByResource.ContainsKey(resource.Metadata.Id))
                {
                    publicationsByResource[resource.Metadata.Id] = (publication, service, resource);
                }
            }

            var visiblePublications = publicationsByResource.Values.ToList();
            var collections = await ProjectWithLimitedConcurrencyAsync(
                visiblePublications,
                (entry, ct) => CreateCollectionAsync(
                    entry.Resource,
                    entry.Publication,
                    entry.Service,
                    snapshot,
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
        [FromServices] IMetadataV2GraphProvider graphProvider,
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

            var validation = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
                context,
                collectionId,
                requiredProtocol: OgcFeaturesProtocolName,
                cancellationToken: cancellationToken);
            if (!validation.IsValid)
            {
                return validation.ErrorResult!;
            }

            var resource = validation.Resource!;
            var publication = validation.Publication!;
            var service = validation.Service;
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            var collection = await CreateCollectionAsync(
                resource,
                publication,
                service,
                snapshot,
                baseUrl,
                featureReader,
                crsRegistry,
                coordinateTransformService,
                cancellationToken);
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

            OgcFeaturesLog.CollectionReturned(logger, collectionId, resource.Metadata.Name);
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
                requiredProtocol: OgcFeaturesProtocolName,
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
    /// Metadata v2 builder for an OGC API Features <see cref="CollectionInfo"/>. The collection
    /// id is the publication's <c>ServiceLocalId</c>, falling back to its resource name when the
    /// publication carries no explicit local id. Spatial extent is read from the typed
    /// <see cref="MetadataV2ResourceSpatial"/> slot on the resource; temporal extent is computed
    /// from the V2 temporal helpers keyed on the resolved storage layer id.
    /// </summary>
    private static async Task<CollectionInfo> CreateCollectionAsync(
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        MetadataV2Service? service,
        MetadataV2GraphSnapshot snapshot,
        string baseUrl,
        IFeatureReader featureReader,
        ICrsRegistry crsRegistry,
        ICoordinateTransformService coordinateTransformService,
        CancellationToken cancellationToken)
    {
        var collectionId = publication.ServiceLocalId
            ?? publication.Path
            ?? resource.Metadata.Name;
        var displayName = publication.TitleOverride
            ?? resource.Metadata.Title
            ?? resource.Metadata.Name;
        var description = resource.Metadata.Description;
        var itemsBaseHref = $"{baseUrl}/ogc/features/collections/{Uri.EscapeDataString(collectionId)}/items";
        var collectionSegment = Uri.EscapeDataString(collectionId);
        var collectionLinks = ImmutableArray.CreateBuilder<Link>();

        // Self link
        collectionLinks.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionSegment}",
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: displayName));

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

        if (IsProtocolEnabled(service, OgcApiMapsProtocolName))
        {
            collectionLinks.Add(Link.Create(
                href: $"{baseUrl}/ogc/maps/collections/{collectionSegment}/map",
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
            href: $"{baseUrl}/ogc/features/collections/{collectionSegment}/queryables",
            rel: RelationTypes.Queryables,
            type: MediaTypes.SchemaJson,
            title: "Queryables"));

        // Style link (MapLibre style JSON) — uses the storage layer id as the v1 catalog does.
        var storageLayerId = snapshot.ResolveStorageLayerId(publication);
        if (storageLayerId.HasValue)
        {
            collectionLinks.Add(Link.Create(
                href: $"{baseUrl}/api/styles/{storageLayerId.Value.ToString(CultureInfo.InvariantCulture)}.json",
                rel: RelationTypes.Style,
                type: MediaTypes.Json,
                title: "Style"));
        }

        if (IsProtocolEnabled(service, OgcApiTilesProtocolName))
        {
            collectionLinks.Add(Link.Create(
                href: $"{baseUrl}/ogc/tiles/collections/{collectionSegment}/tiles",
                rel: RelationTypes.TilesetsVector,
                type: MediaTypes.Json,
                title: "Vector tilesets"));
        }

        SpatialExtent? spatialExtent = null;
        var bbox = resource.ReadBbox();
        if (bbox is not null)
        {
            var extentSrid = resource.ReadSrid() ?? 4326;
            (double Lon, double Lat) min;
            (double Lon, double Lat) max;
            var transformedToCrs84 = false;
            (double Lon, double Lat) minTransformed = default;
            (double Lon, double Lat) maxTransformed = default;
            if (extentSrid != 4326)
            {
                transformedToCrs84 =
                    OgcExtentTransformer.TryTransformToCrs84(bbox.West, bbox.South, extentSrid, out minTransformed) &&
                    OgcExtentTransformer.TryTransformToCrs84(bbox.East, bbox.North, extentSrid, out maxTransformed);

                if (!transformedToCrs84)
                {
                    var extentResult = await coordinateTransformService.TransformExtentAsync(
                        bbox.West, bbox.South,
                        bbox.East, bbox.North,
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
                    min = (bbox.West, bbox.South);
                    max = (bbox.East, bbox.North);
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

        TemporalExtent? temporalExtent = null;
        if (storageLayerId.HasValue)
        {
            temporalExtent = await OgcFeaturesUtilities.BuildTemporalExtentAsync(
                resource,
                storageLayerId.Value,
                featureReader,
                cancellationToken).ConfigureAwait(false);
        }

        var extent = spatialExtent == null && temporalExtent == null
            ? null
            : new Extent
            {
                Spatial = spatialExtent,
                Temporal = temporalExtent
            };

        CrsDefinition? storageCrsDefinition = null;
        var resourceSrid = resource.ReadSrid();
        if (resourceSrid.HasValue)
        {
            storageCrsDefinition = await crsRegistry.ResolveAsync(
                resourceSrid.Value.ToOgcCrs(),
                cancellationToken);
        }
        var supportedCrs = await OgcFeaturesUtilities.GetSupportedCrsUrisAsync(
            resource,
            crsRegistry,
            cancellationToken);

        return new CollectionInfo
        {
            Id = collectionId,
            Title = displayName,
            Description = description,
            Links = collectionLinks.ToImmutable(),
            Extent = extent,
            Crs = supportedCrs,
            StorageCrs = storageCrsDefinition?.Uri
        };
    }

    /// <summary>
    /// Builds the OGC API Features queryables JSON Schema from
    /// <see cref="MetadataV2Resource.SchemaFields"/>, with the primary geometry field
    /// resolved via <see cref="MetadataV2SpatialExtensions.FindPrimaryGeometryField"/>.
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
    /// Builds a queryables JSON Schema property from a V2 schema field.
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
    /// Maps a V2 <see cref="MetadataV2FieldType"/> to a JSON Schema type/format pair.
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
                ErrorResult: StandardErrorHelpers.CreateBadRequest(
                    context,
                    collectionResult.ErrorMessage ?? "Collection ID is required."));
        }

        var resolvedCollectionId = collectionResult.Value!;
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var validationResult = await resourceValidator.ValidateCollectionV2Async(resolvedCollectionId, cancellationToken);
        if (!validationResult.IsValid)
        {
            var errorResult = validationResult.ErrorCode == ResourceValidationError.InvalidIdentifier
                ? StandardErrorHelpers.CreateBadRequest(context, validationResult.ErrorMessage ?? "Invalid collection ID.")
                : null;
            return new CollectionResolution(
                Found: false,
                ResolvedCollectionId: resolvedCollectionId,
                ErrorResult: errorResult);
        }

        return new CollectionResolution(
            Found: true,
            ResolvedCollectionId: resolvedCollectionId,
            ErrorResult: null);
    }

    private readonly record struct CollectionResolution(
        bool Found,
        string ResolvedCollectionId,
        IResult? ErrorResult);

    private static bool IsProtocolEnabled(MetadataV2Service? service, string protocol)
        => service?.Protocols.Any(enabled => string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase)) == true;

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
