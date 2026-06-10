// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Stac.Models;
using Honua.Protocols.Stac.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Stac;

/// <summary>
/// STAC collection listing and detail endpoints.
/// </summary>
internal static class CollectionEndpoints
{
    /// <summary>
    /// Maps STAC collection endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapStacCollectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/stac/collections", HandleGetCollections)
            .WithDisplayName("STAC Collections")
            .WithName("StacCollections")
            .WithSummary("Get all STAC collections")
            .WithDescription("Lists all available STAC collections")
            .WithTags("STAC")
            .CacheOutput("StacCollections")
            .WithETag()
            .Produces<StacCollectionsResponse>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/stac/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("STAC Collection")
            .WithName("StacCollection")
            .WithSummary("Get a STAC collection by ID")
            .WithDescription("Returns a single STAC collection with extent and links")
            .WithTags("STAC")
            .CacheOutput("StacCollection")
            .WithETag()
            .Produces<StacCollection>(200, MediaTypes.Json)
            .Produces(404);

        // The Filter Extension (https://api.stacspec.org/v1.0.0/item-search#filter) requires both
        // of these queryables endpoints to be present.  They describe the filterable properties
        // of the catalog / a specific collection as a JSON Schema document.
        endpoints.MapGet("/stac/queryables", HandleGetCatalogQueryables)
            .WithDisplayName("STAC Catalog Queryables")
            .WithName("StacCatalogQueryables")
            .WithSummary("Get queryable properties for all STAC collections")
            .WithDescription("Returns a JSON Schema document describing the properties that can be used in CQL2 filter expressions across all collections")
            .WithTags("STAC")
            .Produces<QueryablesSchema>(200, MediaTypes.SchemaJson)
            .Produces<QueryablesSchema>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/stac/collections/{collectionId}/queryables", HandleGetCollectionQueryables)
            .WithDisplayName("STAC Collection Queryables")
            .WithName("StacCollectionQueryables")
            .WithSummary("Get queryable properties for a specific STAC collection")
            .WithDescription("Returns a JSON Schema document describing the properties that can be used in CQL2 filter expressions for the specified collection")
            .WithTags("STAC")
            .Produces<QueryablesSchema>(200, MediaTypes.SchemaJson)
            .Produces<QueryablesSchema>(200, MediaTypes.Json)
            .Produces(404);

        return endpoints;
    }

    private static async Task<IResult> HandleGetCollections(
        HttpContext context,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICoordinateTransformService? coordinateTransformService,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.Collections,
            "/stac/collections",
            HttpMethods.Get);
        StacLog.CollectionsRequested(logger);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Collections);
        if (validationError is not null)
        {
            StacTelemetry.SetFailed(activity, "invalid_query_parameters");
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var stacBase = $"{baseUrl}/stac";

            var visible = await StacV2Lookups.ResolveVisibleStacPublicationsAsync(context, cancellationToken)
                .ConfigureAwait(false);

            var collections = await CollectionsEndpoints.ProjectWithLimitedConcurrencyAsync(
                visible,
                (resolved, ct) => StacMappingService.MapResourceToCollectionAsync(
                    resolved.Resource,
                    resolved.Publication,
                    resolved.LayerIndex,
                    featureReader,
                    baseUrl,
                    coordinateTransformService,
                    ct),
                cancellationToken).ConfigureAwait(false);

            var links = ImmutableArray.Create(
                Link.Create(
                    href: $"{stacBase}/collections",
                    rel: RelationTypes.Self,
                    type: MediaTypes.Json,
                    title: "Collections"),
                Link.Create(
                    href: stacBase,
                    rel: StacConstants.StacRelations.Root,
                    type: MediaTypes.Json,
                    title: "STAC Catalog"));

            var response = new StacCollectionsResponse
            {
                Collections = collections,
                Links = links
            };

            StacLog.CollectionsReturned(logger, collections.Length);
            StacTelemetry.SetResultCount(activity, collections.Length);
            return Results.Json(response, StacJsonContext.Default.StacCollectionsResponse, MediaTypes.Json);
        }
        catch (OperationCanceledException ex)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            StacTelemetry.RecordException(activity, ex);
            throw;
        }
        catch (Exception ex)
        {
            StacTelemetry.RecordException(activity, ex);
            StacLog.OperationFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving STAC collections.");
        }
    }

    private static async Task<IResult> HandleGetCollection(
        string collectionId,
        HttpContext context,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ICoordinateTransformService? coordinateTransformService,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.Collection,
            "/stac/collections/{collectionId}",
            HttpMethods.Get,
            collectionId);
        StacLog.CollectionRequested(logger, collectionId);

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

            var resolved = await StacV2Lookups.ResolveStacPublicationAsync(
                context, collectionId, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                StacTelemetry.SetFailed(activity, "collection_not_found");
                StacLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var accessError = Honua.Infrastructure.Authentication.AccessPolicyHelpers
                .RequireResourceAccess(context, resolved.Value.Resource, resolved.Value.Service);
            if (accessError != null)
            {
                StacTelemetry.SetFailed(activity, "collection_forbidden");
                return accessError;
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var collection = await StacMappingService.MapResourceToCollectionAsync(
                resolved.Value.Resource,
                resolved.Value.Publication,
                resolved.Value.LayerIndex,
                featureReader,
                baseUrl,
                coordinateTransformService,
                cancellationToken).ConfigureAwait(false);

            StacTelemetry.SetResultCount(activity, 1);
            return Results.Json(collection, StacJsonContext.Default.StacCollection, MediaTypes.Json);
        }
        catch (OperationCanceledException ex)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            StacTelemetry.RecordException(activity, ex);
            throw;
        }
        catch (Exception ex)
        {
            StacTelemetry.RecordException(activity, ex);
            StacLog.OperationFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving the STAC collection.");
        }
    }

    private static Task<IResult> HandleGetCatalogQueryables(
        HttpContext context,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        try
        {
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var queryablesId = $"{baseUrl}/stac/queryables";

            // Return a minimal catalog-level queryables document describing properties
            // that are common across all STAC collections (STAC common metadata fields).
            var schema = BuildCatalogQueryablesSchema(queryablesId);

            return Task.FromResult(Results.Json(schema, OgcJsonContext.Default.QueryablesSchema, MediaTypes.SchemaJson));
        }
        catch (Exception ex)
        {
            StacLog.OperationFailed(logger, ex);
            return Task.FromResult(StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving the STAC catalog queryables."));
        }
    }

    private static async Task<IResult> HandleGetCollectionQueryables(
        string collectionId,
        HttpContext context,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

            var resolved = await StacV2Lookups.ResolveStacPublicationAsync(
                context, collectionId, cancellationToken).ConfigureAwait(false);

            if (resolved is null)
            {
                StacLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var escapedCollectionId = Uri.EscapeDataString(collectionId);
            var queryablesId = $"{baseUrl}/stac/collections/{escapedCollectionId}/queryables";

            var schema = BuildCollectionQueryablesSchema(resolved.Value.Resource, queryablesId);

            return Results.Json(schema, OgcJsonContext.Default.QueryablesSchema, MediaTypes.SchemaJson);
        }
        catch (OperationCanceledException)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StacLog.OperationFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving the STAC collection queryables.");
        }
    }

    /// <summary>
    /// Builds a catalog-level queryables document listing the STAC common metadata properties
    /// that are filterable across all collections.
    /// </summary>
    private static QueryablesSchema BuildCatalogQueryablesSchema(string schemaId)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, JsonSchemaProperty>();

        properties["datetime"] = new JsonSchemaProperty
        {
            Type = "string",
            Format = "date-time",
            Title = "Date and Time",
            Description = "The searchable date and time of the resource in UTC (RFC 3339)"
        };
        properties["geometry"] = new JsonSchemaProperty
        {
            Type = "object",
            Title = "Geometry",
            Description = "Geometric representation of the item",
            Format = "geometry",
            Ref = "https://geojson.org/schema/Geometry.json"
        };
        properties["id"] = new JsonSchemaProperty
        {
            Type = "string",
            Title = "Item ID",
            Description = "Unique provider-assigned identifier for the item"
        };
        properties["collection"] = new JsonSchemaProperty
        {
            Type = "string",
            Title = "Collection",
            Description = "The identifier of the STAC collection this item belongs to"
        };

        return new QueryablesSchema
        {
            Id = schemaId,
            Type = "object",
            Title = "STAC Catalog Queryables",
            Description = "Common queryable properties available across all STAC collections",
            Properties = properties.ToImmutable()
        };
    }

    /// <summary>
    /// Builds a per-collection queryables document derived from the V2 resource schema fields.
    /// </summary>
    private static QueryablesSchema BuildCollectionQueryablesSchema(
        MetadataV2Resource resource,
        string schemaId)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, JsonSchemaProperty>();
        var requiredFields = new List<string>();
        var geometryField = resource.FindPrimaryGeometryField();
        var geometryFieldName = geometryField?.Name;

        foreach (var field in resource.SchemaFields ?? Array.Empty<MetadataV2Field>())
        {
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

        var displayName = resource.Metadata.Title ?? resource.Metadata.Name ?? schemaId;

        return new QueryablesSchema
        {
            Id = schemaId,
            Type = "object",
            Title = $"Queryables for {displayName}",
            Description = $"Schema for queryable properties of the {displayName} collection",
            Properties = properties.ToImmutable(),
            Required = requiredFields.Count > 0
                ? requiredFields.ToImmutableArray()
                : (ImmutableArray<string>?)null
        };
    }

    /// <summary>
    /// Converts a V2 schema field to a JSON Schema property definition.
    /// </summary>
    private static JsonSchemaProperty ConvertFieldToJsonSchemaProperty(MetadataV2Field field)
    {
        var (type, format) = field.Type switch
        {
            MetadataV2FieldType.String => ("string", (string?)null),
            MetadataV2FieldType.Integer => ("integer", (string?)null),
            MetadataV2FieldType.BigInteger => ("integer", (string?)null),
            MetadataV2FieldType.Double => ("number", "double"),
            MetadataV2FieldType.Float => ("number", "float"),
            MetadataV2FieldType.Boolean => ("boolean", (string?)null),
            MetadataV2FieldType.DateTime => ("string", "date-time"),
            MetadataV2FieldType.Date => ("string", "date"),
            MetadataV2FieldType.Time => ("string", "time"),
            MetadataV2FieldType.Uuid => ("string", "uuid"),
            _ => ("string", (string?)null)
        };

        return new JsonSchemaProperty
        {
            Type = type,
            Format = format,
            Title = field.Title ?? field.Name,
            Description = field.Description
        };
    }
}
