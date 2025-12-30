// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Collections management endpoints for OGC API Features
/// </summary>
internal static class CollectionsEndpoints
{
    /// <summary>
    /// Maps collections management endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapCollectionsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features/collections", HandleGetCollections)
            .WithDisplayName("OGC API Features Collections")
            .WithName("CollectionInfos")
            .WithSummary("Get OGC API Features collections")
            .WithDescription("Lists all available feature collections")
            .WithTags("OGC API Features")
            .CacheOutput("OgcCollections")
            .Produces<Collections>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("OGC API Features Collection")
            .WithName("CollectionInfo")
            .WithSummary("Get OGC API Features collection metadata")
            .WithDescription("Get detailed metadata for a specific collection")
            .WithTags("OGC API Features")
            .CacheOutput("OgcCollection")
            .Produces<CollectionInfo>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}/queryables", HandleGetQueryables)
            .WithDisplayName("OGC API Features Queryables")
            .WithName("Queryables")
            .WithSummary("Get OGC API Features queryables schema")
            .WithDescription("Get the schema for queryable properties of a collection")
            .WithTags("OGC API Features")
            .CacheOutput("OgcQueryables")
            .Produces<QueryablesSchema>(200, MediaTypes.Json)
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
        ILayerCatalog layerCatalog,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            var cancellationToken = GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var collections = layers.Select(layer => CreateCollection(layer, baseUrl)).ToImmutableArray();

            var links = OgcFeaturesUtilities.BuildFormatLinks(
                    request,
                    $"{baseUrl}/ogc/features/collections",
                    outputFormat,
                    OgcFeaturesUtilities.MetadataFormats,
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

            return OgcFeaturesUtilities.FormatMetadataResponse(response, OgcJsonContext.Default.Collections, outputFormat, "Collections");
        }
        catch (ArgumentException ex)
        {
            // Note: Using static reference to logging from main endpoints class
            CollectionsEndpointLogging.LogInvalidCollectionsRequest(logger, ex);
            return GeoServicesErrorHelpers.CreateBadRequestError("Invalid request parameters.");
        }
        catch (InvalidOperationException ex)
        {
            CollectionsEndpointLogging.LogInvalidCollectionsOperation(logger, ex);
            return GeoServicesErrorHelpers.CreateBadRequestError("Invalid operation.");
        }
        catch (Exception ex)
        {
            CollectionsEndpointLogging.LogCollectionsQueryFailed(logger, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500,
                "Internal server error",
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
        ILayerCatalog layerCatalog,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = OgcFeaturesUtilities.ValidateQueryParameters(request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            if (!int.TryParse(collectionId, out var layerId))
            {
                return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
            }

            var cancellationToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
            }

            var collection = CreateCollection(layer, baseUrl);
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}";
            var selfHref = $"{basePath}{request.QueryString}";
            var updatedLinks = collection.Links.Select(link =>
                    string.Equals(link.Rel, RelationTypes.Self, StringComparison.OrdinalIgnoreCase)
                        ? link with { Href = selfHref, Type = outputFormat }
                        : link)
                .ToImmutableArray();

            updatedLinks = OgcFeaturesUtilities.AddAlternateLinks(updatedLinks, request, basePath, outputFormat, OgcFeaturesUtilities.MetadataFormats);
            collection = collection with { Links = updatedLinks };

            return OgcFeaturesUtilities.FormatMetadataResponse(
                collection,
                OgcJsonContext.Default.CollectionInfo,
                outputFormat,
                collection.Title ?? collection.Id);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("parse") || ex.Message.Contains("invalid"))
        {
            CollectionsEndpointLogging.LogInvalidCollectionId(logger, collectionId, ex);
            return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
        }
        catch (InvalidOperationException)
        {
            // Layer not found is a legitimate 404 case
            return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
        }
        catch (Exception ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500,
                "Internal server error",
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
        ILayerCatalog layerCatalog,
        ILogger<OgcFeaturesEndpoints.OgcFeaturesEndpointsLog> logger)
    {
        try
        {
            var validationError = OgcFeaturesUtilities.ValidateQueryParameters(context.Request, OgcFeaturesUtilities.AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(validationError.Value ?? "Invalid query parameters.");
            }

            if (!OgcFeaturesUtilities.TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return CreateFormatError(context, formatError);
            }

            if (!int.TryParse(collectionId, out var layerId))
            {
                return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
            }

            // Verify collection/layer exists
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
            }

            // Build queryables schema from layer fields
            var queryables = CreateQueryablesSchema(layer);

            return OgcFeaturesUtilities.FormatMetadataResponse(queryables, OgcJsonContext.Default.QueryablesSchema, outputFormat, "Queryables");
        }
        catch (ArgumentException ex) when (ex.Message.Contains("parse") || ex.Message.Contains("invalid"))
        {
            CollectionsEndpointLogging.LogInvalidCollectionId(logger, collectionId, ex);
            return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
        }
        catch (InvalidOperationException)
        {
            return GeoServicesErrorHelpers.CreateNotFoundError($"Collection '{collectionId}' not found.");
        }
        catch (Exception ex)
        {
            CollectionsEndpointLogging.LogCollectionQueryFailed(logger, collectionId, ex);
            return ProtocolErrorWriter.CreateErrorResult(context, 500,
                "Internal server error",
                "An error occurred while retrieving the queryables schema.");
        }
    }

    /// <summary>
    /// Converts a layer definition to OGC API Features collection
    /// </summary>
    private static CollectionInfo CreateCollection(LayerDefinition layer, string baseUrl)
    {
        // Use layer ID as collection ID (string representation)
        var collectionId = layer.Id.ToString();
        var collectionLinks = ImmutableArray.Create(
            // Self link
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: layer.Name
            ),

            // Items link
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/items",
                rel: RelationTypes.Items,
                type: MediaTypes.GeoJson,
                title: "Items"
            ),

            // Data link (alternate to items)
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/items",
                rel: RelationTypes.Data,
                type: MediaTypes.GeoJson,
                title: "Data"
            ),

            // Parent (collections)
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections",
                rel: "parent",
                type: MediaTypes.Json,
                title: "Collections"
            ),

            // Queryables link
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/queryables",
                rel: RelationTypes.Queryables,
                type: MediaTypes.Json,
                title: "Queryables"
            )
        );

        return new CollectionInfo
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description,
            Links = collectionLinks,
            Extent = layer.Extent != null ? new Extent
            {
                Spatial = new SpatialExtent
                {
                    BoundingBox = ImmutableArray.Create(ImmutableArray.Create(
                        layer.Extent.Value.MinX,
                        layer.Extent.Value.MinY,
                        layer.Extent.Value.MaxX,
                        layer.Extent.Value.MaxY)),
                    Crs = layer.SpatialReference.Srid.ToOgcCrs()
                }
            } : null,
            Crs = ImmutableArray.Create(
                OgcFeaturesUtilities.Crs84Uri,
                OgcFeaturesUtilities.Epsg4326Uri
            ),
            StorageCrs = layer.SpatialReference.Srid.ToOgcCrs()
        };
    }

    /// <summary>
    /// Creates queryables schema from layer definition
    /// </summary>
    private static QueryablesSchema CreateQueryablesSchema(LayerDefinition layer)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, JsonSchemaProperty>();
        var requiredFields = new List<string>();

        // Add properties for all non-geometry fields
        foreach (var field in layer.AttributeFields.Where(OgcFeaturesUtilities.IsSimpleQueryableField))
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
                Description = "Geometric representation of the feature"
            };
        }

        return new QueryablesSchema
        {
            Type = "object",
            Title = $"Queryables for {layer.Name}",
            Description = $"Schema for queryable properties of the {layer.Name} collection",
            Properties = properties.ToImmutable(),
            Required = requiredFields.ToImmutableArray()
        };
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
    /// Creates a standardized error response for invalid format negotiation.
    /// </summary>
    private static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return GeoServicesErrorHelpers.CreateBadRequestError(badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return ProtocolErrorWriter.CreateErrorResult(
                context,
                statusCodeResult.StatusCode.Value,
                "Not Acceptable",
                "Requested format is not acceptable.");
        }

        return GeoServicesErrorHelpers.CreateBadRequestError("Invalid format.");
    }

    /// <summary>
    /// Gets timeout-aware cancellation token from context
    /// </summary>
    private static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }
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

    [LoggerMessage(EventId = 5204, Level = LogLevel.Warning,
        Message = "Invalid collection ID provided: {CollectionId}")]
    public static partial void LogInvalidCollectionId(ILogger logger, string collectionId, Exception exception);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Error,
        Message = "Collection query failed for ID: {CollectionId}")]
    public static partial void LogCollectionQueryFailed(ILogger logger, string collectionId, Exception exception);
}
