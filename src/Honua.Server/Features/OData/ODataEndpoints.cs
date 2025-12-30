// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Honua.Server.Features.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.OData;

/// <summary>
/// OData v4 endpoints providing intermediate conformance level.
/// Supports $filter, $select, $orderby, $top, $skip, $count, and CRUD operations.
/// </summary>
internal static partial class ODataEndpoints
{
    internal sealed class ODataEndpointsLog
    {
    }

    /// <summary>
    /// OData protocol version
    /// </summary>
    private const string ODataVersion = "4.0";

    /// <summary>
    /// OData JSON content type with minimal metadata
    /// </summary>
    private const string ODataContentType = "application/json;odata.metadata=minimal";

    /// <summary>
    /// Maps OData v4 endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapODataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // OData service document
        endpoints.MapGet("/odata", HandleGetServiceDocument)
            .WithDisplayName("OData Service Document")
            .WithName("ODataServiceDocument")
            .WithSummary("Get OData service document")
            .WithTags("OData")
            .Produces<ServiceDocument>(200, "application/json")
            .Produces(404);

        // OData metadata document
        endpoints.MapGet("/odata/$metadata", HandleGetMetadata)
            .WithDisplayName("OData Metadata Document")
            .WithName("ODataMetadata")
            .WithSummary("Get OData metadata document")
            .WithTags("OData")
            .Produces<string>(200, "application/xml")
            .Produces(404);

        // OData entity sets (layers as collections)
        endpoints.MapGet("/odata/Layers", HandleGetLayers)
            .WithDisplayName("OData Layers Collection")
            .WithName("ODataLayers")
            .WithSummary("Get layers collection with OData query parameters")
            .WithTags("OData")
            .Produces<ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // OData features for a specific layer
        endpoints.MapGet("/odata/Features({layerId:int})", HandleGetFeatures)
            .WithDisplayName("OData Features Collection")
            .WithName("ODataFeatures")
            .WithSummary("Get features with OData query parameters ($filter, $select, $orderby, $top, $skip, $count)")
            .WithTags("OData")
            .Produces<ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // POST - Create a new feature
        endpoints.MapPost("/odata/Features({layerId:int})", HandleCreateFeature)
            .WithDisplayName("OData Create Feature")
            .WithName("ODataCreateFeature")
            .WithSummary("Create a new feature in the specified layer")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(201, "application/json")
            .Produces(400)
            .Produces(404);

        // GET - Get a single feature
        endpoints.MapGet("/odata/Features({layerId:int},{objectId:long})", HandleGetSingleFeature)
            .WithDisplayName("OData Get Feature")
            .WithName("ODataGetFeature")
            .WithSummary("Get a single feature by ID")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(404);

        // PATCH - Update an existing feature
        endpoints.MapPatch("/odata/Features({layerId:int},{objectId:long})", HandleUpdateFeature)
            .WithDisplayName("OData Update Feature")
            .WithName("ODataUpdateFeature")
            .WithSummary("Update an existing feature")
            .WithTags("OData")
            .Produces<Dictionary<string, object?>>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // DELETE - Delete a feature
        endpoints.MapDelete("/odata/Features({layerId:int},{objectId:long})", HandleDeleteFeature)
            .WithDisplayName("OData Delete Feature")
            .WithName("ODataDeleteFeature")
            .WithSummary("Delete a feature")
            .WithTags("OData")
            .Produces(204)
            .Produces(404);

        // POST - Batch operations
        endpoints.MapPost("/odata/$batch", HandleBatch)
            .WithDisplayName("OData Batch Operations")
            .WithName("ODataBatch")
            .WithSummary("Execute multiple operations in a single request with optional atomicity groups")
            .WithTags("OData")
            .Produces<ODataBatchResponse>(200, "application/json")
            .Produces(400);

        // GET - Aggregation with $apply
        endpoints.MapGet("/odata/Features({layerId:int})/$apply", HandleApply)
            .WithDisplayName("OData Aggregation")
            .WithName("ODataApply")
            .WithSummary("Aggregate features using $apply transformations (aggregate, groupby, filter, compute)")
            .WithTags("OData")
            .Produces<ODataAggregationResult>(200, "application/json")
            .Produces(400)
            .Produces(404);

        // GET - Full-text search with $search
        endpoints.MapGet("/odata/Features({layerId:int})/$search", HandleSearch)
            .WithDisplayName("OData Search")
            .WithName("ODataSearch")
            .WithSummary("Full-text search across feature attributes using PostgreSQL text search")
            .WithTags("OData")
            .Produces<ODataSearchResult>(200, "application/json")
            .Produces(400)
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles OData service document request
    /// </summary>
    private static IResult HandleGetServiceDocument(HttpContext context)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var serviceDocument = new ServiceDocument
        {
            Context = $"{baseUrl}/odata/$metadata",
            Value = new[]
            {
                new EntitySet
                {
                    Name = "Layers",
                    Url = "Layers"
                },
                new EntitySet
                {
                    Name = "Features",
                    Url = "Features"
                }
            }
        };

        SetODataHeaders(context);
        return Results.Json(serviceDocument, ODataJsonContext.Default.ServiceDocument, contentType: ODataContentType);
    }

    /// <summary>
    /// Sets required OData headers on the response
    /// </summary>
    private static void SetODataHeaders(HttpContext context, string? etag = null)
    {
        context.Response.Headers["OData-Version"] = ODataVersion;
        if (etag != null)
        {
            context.Response.Headers.ETag = $"\"{etag}\"";
        }
    }

    /// <summary>
    /// Handles OData metadata document request
    /// </summary>
    private static async Task<IResult> HandleGetMetadata(
        HttpContext context,
        ILayerCatalog layerCatalog,
        ILogger<ODataEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        SetODataHeaders(context);
        try
        {
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(effectiveToken);
            var metadata = GenerateODataMetadata(layers.ToArray());
            return TypedResults.Content(metadata, "application/xml");
        }
        catch (Exception ex)
        {
            Log.MetadataFallback(logger, ex);
            // Fall back to static metadata if layer retrieval fails
            var staticMetadata = GetStaticMetadata();
            return TypedResults.Content(staticMetadata, "application/xml");
        }
    }

    /// <summary>
    /// Generates dynamic OData CSDL metadata from layer definitions
    /// </summary>
    private static string GenerateODataMetadata(LayerDefinition[] layers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">""");
        sb.AppendLine("  <edmx:DataServices>");
        sb.AppendLine("""    <Schema Namespace="Honua" xmlns="http://docs.oasis-open.org/odata/ns/edm">""");

        // Base Layer entity type
        sb.AppendLine("      <EntityType Name=\"Layer\">");
        sb.AppendLine("        <Key>");
        sb.AppendLine("          <PropertyRef Name=\"Id\"/>");
        sb.AppendLine("        </Key>");
        sb.AppendLine("        <Property Name=\"Id\" Type=\"Edm.Int32\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"Name\" Type=\"Edm.String\"/>");
        sb.AppendLine("        <Property Name=\"Description\" Type=\"Edm.String\"/>");
        sb.AppendLine("        <Property Name=\"GeometryType\" Type=\"Edm.String\"/>");
        sb.AppendLine("      </EntityType>");

        // Base Feature entity type
        sb.AppendLine("      <EntityType Name=\"Feature\">");
        sb.AppendLine("        <Key>");
        sb.AppendLine("          <PropertyRef Name=\"ObjectId\"/>");
        sb.AppendLine("        </Key>");
        sb.AppendLine("        <Property Name=\"ObjectId\" Type=\"Edm.Int64\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"LayerId\" Type=\"Edm.Int32\" Nullable=\"false\"/>");
        sb.AppendLine("        <Property Name=\"Geometry\" Type=\"Edm.Binary\"/>");
        sb.AppendLine("        <Property Name=\"Attributes\" Type=\"Edm.String\"/>");
        sb.AppendLine("      </EntityType>");

        // Generate specific entity types for each layer with their fields
        foreach (var layer in layers)
        {
            var safeLayerName = SanitizeEntityTypeName(layer.Name);
            sb.AppendLine($"      <EntityType Name=\"{safeLayerName}Feature\" BaseType=\"Honua.Feature\">");

            foreach (var field in layer.AttributeFields)
            {
                var edmType = MapFieldTypeToEdm(field.Type);
                var nullable = field.Nullable ? "true" : "false";
                sb.AppendLine($"        <Property Name=\"{field.Name}\" Type=\"{edmType}\" Nullable=\"{nullable}\"/>");
            }

            sb.AppendLine("      </EntityType>");
        }

        // Entity container with entity sets
        sb.AppendLine("      <EntityContainer Name=\"Container\">");
        sb.AppendLine("        <EntitySet Name=\"Layers\" EntityType=\"Honua.Layer\"/>");
        sb.AppendLine("        <EntitySet Name=\"Features\" EntityType=\"Honua.Feature\"/>");

        // Generate layer-specific entity sets
        foreach (var layer in layers)
        {
            var safeLayerName = SanitizeEntityTypeName(layer.Name);
            sb.AppendLine($"        <EntitySet Name=\"{safeLayerName}\" EntityType=\"Honua.{safeLayerName}Feature\"/>");
        }

        sb.AppendLine("      </EntityContainer>");
        sb.AppendLine("    </Schema>");
        sb.AppendLine("  </edmx:DataServices>");
        sb.AppendLine("</edmx:Edmx>");

        return sb.ToString();
    }

    /// <summary>
    /// Returns static fallback metadata
    /// </summary>
    private static string GetStaticMetadata()
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
                <edmx:DataServices>
                    <Schema Namespace="Honua" xmlns="http://docs.oasis-open.org/odata/ns/edm">
                        <EntityType Name="Layer">
                            <Key>
                                <PropertyRef Name="Id"/>
                            </Key>
                            <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                            <Property Name="Name" Type="Edm.String"/>
                            <Property Name="Description" Type="Edm.String"/>
                        </EntityType>
                        <EntityType Name="Feature">
                            <Key>
                                <PropertyRef Name="ObjectId"/>
                            </Key>
                            <Property Name="ObjectId" Type="Edm.Int64" Nullable="false"/>
                            <Property Name="LayerId" Type="Edm.Int32" Nullable="false"/>
                            <Property Name="Geometry" Type="Edm.Binary"/>
                            <Property Name="Attributes" Type="Edm.String"/>
                        </EntityType>
                        <EntityContainer Name="Container">
                            <EntitySet Name="Layers" EntityType="Honua.Layer"/>
                            <EntitySet Name="Features" EntityType="Honua.Feature"/>
                        </EntityContainer>
                    </Schema>
                </edmx:DataServices>
            </edmx:Edmx>
            """;
    }

    /// <summary>
    /// Sanitizes a name to be a valid OData entity type name
    /// </summary>
    private static string SanitizeEntityTypeName(string name)
    {
        // Remove invalid characters, ensure starts with letter
        var sb = new StringBuilder();
        var startedWithLetter = false;

        foreach (var c in name)
        {
            if (char.IsLetter(c))
            {
                sb.Append(c);
                startedWithLetter = true;
            }
            else if (startedWithLetter && (char.IsLetterOrDigit(c) || c == '_'))
            {
                sb.Append(c);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "Entity";
    }

    /// <summary>
    /// Maps a FieldType to an OData EDM type
    /// </summary>
    private static string MapFieldTypeToEdm(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.String => "Edm.String",
            FieldType.Integer => "Edm.Int32",
            FieldType.BigInteger => "Edm.Int64",
            FieldType.Double => "Edm.Double",
            FieldType.Float => "Edm.Single",
            FieldType.Boolean => "Edm.Boolean",
            FieldType.DateTime => "Edm.DateTimeOffset",
            FieldType.Date => "Edm.Date",
            FieldType.Time => "Edm.TimeOfDay",
            FieldType.Geometry => "Edm.Binary",
            FieldType.Json => "Edm.String",
            FieldType.Binary => "Edm.Binary",
            FieldType.Uuid => "Edm.Guid",
            _ => "Edm.String"
        };
    }

    /// <summary>
    /// Handles OData layers collection request
    /// </summary>
    private static async Task<IResult> HandleGetLayers(
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureQueryValidator queryValidator,
        ILogger<ODataEndpointsLog> logger,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$top")] int? top = null,
        [FromQuery(Name = "$skip")] int? skip = null,
        [FromQuery(Name = "$count")] bool? count = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (top.HasValue && top.Value <= 0)
            {
                return CreateODataError(context, "InvalidQueryOption", "$top must be a positive integer.");
            }

            if (skip.HasValue && skip.Value < 0)
            {
                return CreateODataError(context, "InvalidQueryOption", "$skip must be a non-negative integer.");
            }

            var validationResult = queryValidator.ValidateQueryLimits(new QueryParameters
            {
                ResultRecordCount = top,
                ResultOffset = skip
            });

            if (!validationResult.IsValid)
            {
                return CreateODataError(context, "InvalidQueryOption", $"Invalid OData query: {validationResult.ErrorMessage}");
            }

            var validatedParams = validationResult.ValidatedParameters!;

            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(effectiveToken);

            // PERFORMANCE OPTIMIZATION: Use single enumeration instead of multiple ToList() calls
            IEnumerable<LayerDefinition> layerQuery = layers;

            // Apply basic filtering if specified
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerQuery = ApplyBasicFilter(layerQuery, filter);
            }

            // PERFORMANCE OPTIMIZATION: Count before materialization to avoid double enumeration
            long? totalCount = null;
            if (count == true)
            {
                // Only materialize for count if needed
                var layersForCount = layerQuery.ToArray();
                totalCount = layersForCount.Length;
                layerQuery = layersForCount; // Reuse materialized collection
            }

            // Apply skip/top pagination on the query before materialization
            if (validatedParams.ResultOffset.HasValue)
            {
                layerQuery = layerQuery.Skip(validatedParams.ResultOffset.Value);
            }

            if (validatedParams.ResultRecordCount.HasValue)
            {
                layerQuery = layerQuery.Take(validatedParams.ResultRecordCount.Value);
            }

            // PERFORMANCE OPTIMIZATION: Single materialization with projection
            var layerData = layerQuery.Select(l => new Dictionary<string, object?>
            {
                ["Id"] = l.Id,
                ["Name"] = l.Name,
                ["Description"] = l.Description
            }).ToArray();

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? layerData.Cast<object>().ToArray()
                : ApplyFieldSelection(layerData, select);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var response = new ODataResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Layers",
                Count = totalCount,
                Value = result
            };

            SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataResponse, contentType: ODataContentType);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidLayersQuery(logger, ex);
            return CreateODataError(context, "InvalidQuery", "Invalid OData query.");
        }
        catch (Exception ex)
        {
            Log.LayersQueryFailed(logger, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles OData features collection request with full query parameter support
    /// </summary>
    private static async Task<IResult> HandleGetFeatures(
        HttpContext context,
        int layerId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IFeatureQueryValidator queryValidator,
        ILogger<ODataEndpointsLog> logger,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] int? top = null,
        [FromQuery(Name = "$skip")] int? skip = null,
        [FromQuery(Name = "$count")] bool? count = null,
        [FromQuery(Name = "$expand")] string? expand = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (top.HasValue && top.Value <= 0)
            {
                return CreateODataError(context, "InvalidQueryOption", "$top must be a positive integer.");
            }

            if (skip.HasValue && skip.Value < 0)
            {
                return CreateODataError(context, "InvalidQueryOption", "$skip must be a non-negative integer.");
            }

            var validationResult = queryValidator.ValidateQueryLimits(new QueryParameters
            {
                ResultRecordCount = top,
                ResultOffset = skip
            });

            if (!validationResult.IsValid)
            {
                return CreateODataError(context, "InvalidQueryOption", $"Invalid OData query: {validationResult.ErrorMessage}");
            }

            var validatedParams = validationResult.ValidatedParameters!;

            var effectiveToken = GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Layer {layerId} not found", 404);
            }

            // Build feature query from OData parameters
            SpatialFilter? spatialFilter = null;
            var remainingFilter = filter;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (TryExtractSpatialFilter(filter, out var parsedSpatialFilter, out var nonSpatialFilter, out var spatialError))
                {
                    spatialFilter = parsedSpatialFilter;
                    remainingFilter = nonSpatialFilter;
                }
                else if (spatialError != null)
                {
                    return CreateODataError(context, "InvalidQuery", spatialError);
                }
            }

            var (sqlFragment, whereClause) = ConvertODataFilterToSqlFragment(remainingFilter);
            var featureQuery = new FeatureQuery
            {
                Where = whereClause,
                SqlFilter = sqlFragment,
                SpatialFilter = spatialFilter,
                SpatialReferenceSrid = layer.SpatialReference.Srid,
                OrderBy = ParseODataOrderBy(orderby, layer),
                Limit = validatedParams.ResultRecordCount,
                Offset = validatedParams.ResultOffset
            };

            // Execute query
            var queryResult = await featureStore.QueryAsync(layerId, featureQuery, effectiveToken);

            // Process $expand for related entities
            Dictionary<long, Dictionary<string, object?[]>>? expandedRelations = null;
            if (!string.IsNullOrWhiteSpace(expand) && layer.HasRelationships)
            {
                expandedRelations = await ProcessExpandAsync(
                    expand,
                    layer,
                    queryResult.Items.Select(f => f.Id).ToArray(),
                    featureStore,
                    effectiveToken);
            }

            // Convert features to OData format
            var featuresData = queryResult.Items.Select(f =>
            {
                var dict = new Dictionary<string, object?>
                {
                    ["ObjectId"] = f.Id,
                    ["LayerId"] = layerId,
                    ["Geometry"] = f.Geometry != null ? Convert.ToBase64String(f.Geometry) : null,
                    ["Attributes"] = SerializeAttributes(f.Attributes)
                };

                // Add expanded relations if available
                if (expandedRelations != null && expandedRelations.TryGetValue(f.Id, out var relations))
                {
                    foreach (var kvp in relations)
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }

                return dict;
            }).ToArray();

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? featuresData.Cast<object>().ToArray()
                : ApplyFieldSelection(featuresData, select);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

            // Calculate @odata.nextLink if there are more results
            string? nextLink = null;
            var currentSkip = skip ?? 0;
            var currentTop = validatedParams.ResultRecordCount ?? 1000;
            var totalItems = queryResult.TotalCount;
            if (currentSkip + result.Length < totalItems)
            {
                nextLink = GenerateNextLink(context.Request, layerId, currentSkip + currentTop, currentTop, filter, select, orderby, count);
            }

            var response = new ODataResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features",
                Count = count == true ? queryResult.TotalCount : null,
                NextLink = nextLink,
                Value = result
            };

            SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataResponse, contentType: ODataContentType);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidFeaturesQuery(logger, layerId, ex);
            return CreateODataError(context, "InvalidQuery", "Invalid OData query.");
        }
        catch (Exception ex)
        {
            Log.FeaturesQueryFailed(logger, layerId, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles getting a single feature by ID
    /// </summary>
    private static async Task<IResult> HandleGetSingleFeature(
        HttpContext context,
        int layerId,
        long objectId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<ODataEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveToken = GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Layer {layerId} not found", 404);
            }

            // Get the feature
            var feature = await featureStore.GetAsync(layerId, objectId, effectiveToken);
            if (feature == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}", 404);
            }

            // Feature is guaranteed to be non-null after the null check
            var featureValue = feature.Value;
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var response = new ODataFeatureResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features/$entity",
                ObjectId = featureValue.Id,
                LayerId = layerId,
                Geometry = featureValue.Geometry != null ? Convert.ToBase64String(featureValue.Geometry) : null,
                Attributes = SerializeAttributes(featureValue.Attributes)
            };

            // Generate ETag from feature ID
            var etag = $"W/\"{featureValue.Id}\"";
            SetODataHeaders(context, featureValue.Id.ToString());
            return Results.Json(response, ODataJsonContext.Default.ODataFeatureResponse, contentType: ODataContentType);
        }
        catch (Exception ex)
        {
            Log.GetFeatureFailed(logger, layerId, objectId, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles creating a new feature
    /// </summary>
    private static async Task<IResult> HandleCreateFeature(
        HttpContext context,
        int layerId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        [FromBody] ODataFeatureRequest request,
        ILogger<ODataEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveToken = GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Layer {layerId} not found", 404);
            }

            // Parse geometry from Base64 WKB if provided
            byte[]? geometry = null;
            if (!string.IsNullOrWhiteSpace(request.Geometry))
            {
                try
                {
                    geometry = Convert.FromBase64String(request.Geometry);
                }
                catch (FormatException)
                {
                    return CreateODataError(context, "InvalidRequest", "Geometry must be a valid Base64-encoded WKB string");
                }

                // Validate WKB geometry
                var validationResult = WkbValidation.Validate(geometry);
                if (!validationResult.IsValid)
                {
                    return CreateODataError(context, "InvalidRequest", $"Invalid geometry: {validationResult.ErrorMessage}");
                }
            }

            // Build attributes
            var attributes = request.Attributes?.ToImmutableDictionary() ?? ImmutableDictionary<string, object?>.Empty;

            // Create the feature
            var newFeature = Feature.Create(0, geometry, attributes);
            var createdFeature = await featureStore.CreateAsync(layerId, newFeature, effectiveToken);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var response = new ODataFeatureResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features/$entity",
                ObjectId = createdFeature.Id,
                LayerId = layerId,
                Geometry = createdFeature.Geometry != null ? Convert.ToBase64String(createdFeature.Geometry) : null,
                Attributes = SerializeAttributes(createdFeature.Attributes)
            };

            // Return 201 Created with Location header and OData-EntityId
            SetODataHeaders(context, createdFeature.Id.ToString());
            context.Response.Headers.Location = $"{baseUrl}/odata/Features({layerId},{createdFeature.Id})";
            context.Response.Headers["OData-EntityId"] = $"{baseUrl}/odata/Features({layerId},{createdFeature.Id})";
            return Results.Json(response, ODataJsonContext.Default.ODataFeatureResponse, contentType: ODataContentType, statusCode: 201);
        }
        catch (ResourceConflictException ex)
        {
            Log.CreateFeatureFailed(logger, layerId, ex);
            return CreateODataError(context, "Conflict", "A conflicting feature already exists.", 409);
        }
        catch (Exception ex)
        {
            Log.CreateFeatureFailed(logger, layerId, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred creating the feature", 500);
        }
    }

    /// <summary>
    /// Handles updating an existing feature
    /// </summary>
    private static async Task<IResult> HandleUpdateFeature(
        HttpContext context,
        int layerId,
        long objectId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        [FromBody] ODataFeatureRequest request,
        ILogger<ODataEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveToken = GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Layer {layerId} not found", 404);
            }

            // Get existing feature to merge with update
            var existingFeature = await featureStore.GetAsync(layerId, objectId, effectiveToken);
            if (existingFeature == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}", 404);
            }

            // Feature is guaranteed to be non-null after the null check
            var existingFeatureValue = existingFeature.Value;

            // Parse geometry from Base64 WKB if provided, otherwise keep existing
            byte[]? geometry = existingFeatureValue.Geometry;
            if (!string.IsNullOrWhiteSpace(request.Geometry))
            {
                try
                {
                    geometry = Convert.FromBase64String(request.Geometry);
                }
                catch (FormatException)
                {
                    return CreateODataError(context, "InvalidRequest", "Geometry must be a valid Base64-encoded WKB string");
                }

                // Validate WKB geometry
                var validationResult = WkbValidation.Validate(geometry);
                if (!validationResult.IsValid)
                {
                    return CreateODataError(context, "InvalidRequest", $"Invalid geometry: {validationResult.ErrorMessage}");
                }
            }

            // Merge attributes - new values override existing
            var attributes = existingFeatureValue.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            if (request.Attributes != null)
            {
                foreach (var kvp in request.Attributes)
                {
                    attributes[kvp.Key] = kvp.Value;
                }
            }

            // Update the feature
            var updatedFeature = Feature.Create(objectId, geometry, attributes.ToImmutableDictionary());
            var result = await featureStore.UpdateAsync(layerId, updatedFeature, effectiveToken);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var response = new ODataFeatureResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features/$entity",
                ObjectId = result.Id,
                LayerId = layerId,
                Geometry = result.Geometry != null ? Convert.ToBase64String(result.Geometry) : null,
                Attributes = SerializeAttributes(result.Attributes)
            };

            SetODataHeaders(context, result.Id.ToString());
            return Results.Json(response, ODataJsonContext.Default.ODataFeatureResponse, contentType: ODataContentType);
        }
        catch (ResourceNotFoundException ex)
        {
            Log.UpdateFeatureNotFound(logger, layerId, objectId, ex);
            return CreateODataError(context, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}", 404);
        }
        catch (ResourceConflictException ex)
        {
            Log.UpdateFeatureFailed(logger, layerId, objectId, ex);
            return CreateODataError(context, "Conflict", "The update conflicted with existing data.", 409);
        }
        catch (Exception ex)
        {
            Log.UpdateFeatureFailed(logger, layerId, objectId, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred updating the feature", 500);
        }
    }

    /// <summary>
    /// Handles deleting a feature
    /// </summary>
    private static async Task<IResult> HandleDeleteFeature(
        HttpContext context,
        int layerId,
        long objectId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<ODataEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveToken = GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Layer {layerId} not found", 404);
            }

            // Delete the feature
            var deleted = await featureStore.DeleteAsync(layerId, objectId, effectiveToken);
            if (!deleted)
            {
                return CreateODataError(context, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}", 404);
            }

            SetODataHeaders(context);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            Log.DeleteFeatureFailed(logger, layerId, objectId, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred deleting the feature", 500);
        }
    }

    /// <summary>
    /// Creates an OData v4 compliant error response.
    /// See: https://docs.oasis-open.org/odata/odata-json-format/v4.01/odata-json-format-v4.01.html#sec_ErrorResponseBody
    /// </summary>
    private static IResult CreateODataError(HttpContext context, string code, string message, int statusCode = 400, string? target = null, ErrorDetail[]? details = null)
    {
        var error = new ODataError
        {
            Error = new ErrorDetails
            {
                Code = code,
                Message = message,
                Details = details
            }
        };

        SetODataHeaders(context);
        return Results.Json(error, ODataJsonContext.Default.ODataError,
            contentType: ODataContentType,
            statusCode: statusCode);
    }

    /// <summary>
    /// Handles OData $batch request for executing multiple operations.
    /// </summary>
    private static async Task<IResult> HandleBatch(
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<ODataEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

            // Read and parse the batch request
            ODataBatchRequest? batchRequest;
            try
            {
                batchRequest = await context.Request.ReadFromJsonAsync<ODataBatchRequest>(
                    ODataJsonContext.Default.ODataBatchRequest,
                    effectiveToken);
            }
            catch (JsonException ex)
            {
                Log.BatchParseFailed(logger, ex);
                return CreateODataError(context, "InvalidRequest", "Failed to parse batch request body.");
            }

            if (batchRequest == null || batchRequest.Requests.IsDefaultOrEmpty)
            {
                return CreateODataError(context, "InvalidRequest", "Batch request must contain at least one request.");
            }

            // Process the batch
            var handler = new ODataBatchHandler(layerCatalog, featureStore, logger);
            var response = await handler.ProcessBatchAsync(batchRequest, baseUrl, effectiveToken);

            SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataBatchResponse, contentType: ODataContentType);
        }
        catch (Exception ex)
        {
            Log.BatchFailed(logger, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred processing the batch request", 500);
        }
    }

    /// <summary>
    /// Handles OData $apply aggregation request.
    /// </summary>
    private static async Task<IResult> HandleApply(
        HttpContext context,
        int layerId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<ODataEndpointsLog> logger,
        [FromQuery(Name = "$apply")] string? apply = null,
        [FromQuery(Name = "$filter")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(apply))
            {
                return CreateODataError(context, "InvalidQueryOption", "$apply parameter is required.");
            }

            var effectiveToken = GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Layer {layerId} not found", 404);
            }

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

            // Process aggregation
            var handler = new ODataAggregationHandler(featureStore);
            var result = await handler.ProcessAggregationAsync(layerId, apply, filter, baseUrl, effectiveToken);

            SetODataHeaders(context);
            return Results.Json(result, ODataJsonContext.Default.ODataAggregationResult, contentType: ODataContentType);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidApplyExpression(logger, layerId, ex);
            return CreateODataError(context, "InvalidQueryOption", $"Invalid $apply expression: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.ApplyFailed(logger, layerId, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred processing the aggregation request", 500);
        }
    }

    /// <summary>
    /// Handles OData $search full-text search request.
    /// </summary>
    private static async Task<IResult> HandleSearch(
        HttpContext context,
        int layerId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<ODataEndpointsLog> logger,
        [FromQuery(Name = "$search")] string? search = null,
        [FromQuery(Name = "$top")] int? top = null,
        [FromQuery(Name = "$skip")] int? skip = null,
        [FromQuery(Name = "$count")] bool? count = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(search))
            {
                return CreateODataError(context, "InvalidQueryOption", "$search parameter is required.");
            }

            var effectiveToken = GetTimeoutAwareCancellationToken(context);

            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return CreateODataError(context, "ResourceNotFound", $"Layer {layerId} not found", 404);
            }

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

            // Build a text search query using PostgreSQL full-text search
            // Convert $search to a SQL text search condition
            var searchTerms = ParseSearchExpression(search);
            var textSearchCondition = BuildTextSearchCondition(searchTerms, layer);

            var query = new FeatureQuery
            {
                SqlFilter = new SqlFragment(textSearchCondition, Array.Empty<object?>()),
                Limit = top ?? 1000,
                Offset = skip
            };

            var result = await featureStore.QueryAsync(layerId, query, effectiveToken);

            // Convert features to OData format
            var featuresData = result.Items.Select(f => new Dictionary<string, object?>
            {
                ["ObjectId"] = f.Id,
                ["LayerId"] = layerId,
                ["Geometry"] = f.Geometry != null ? Convert.ToBase64String(f.Geometry) : null,
                ["Attributes"] = SerializeAttributes(f.Attributes)
            }).ToArray();

            var response = new ODataSearchResult
            {
                Context = $"{baseUrl}/odata/$metadata#Features",
                Count = count == true ? result.TotalCount : null,
                Value = featuresData.Cast<object>().ToArray()
            };

            SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataSearchResult, contentType: ODataContentType);
        }
        catch (Exception ex)
        {
            Log.SearchFailed(logger, layerId, ex);
            return CreateODataError(context, "InternalServerError", "An error occurred processing the search request", 500);
        }
    }

    /// <summary>
    /// Parses an OData $search expression.
    /// Supports: simple terms, quoted phrases, AND, OR, NOT
    /// </summary>
    private static List<List<(string term, bool isNegated, bool isPhrase)>> ParseSearchExpression(string search)
    {
        var termGroups = new List<List<(string term, bool isNegated, bool isPhrase)>>();
        var currentGroup = new List<(string term, bool isNegated, bool isPhrase)>();
        var negate = false;

        var tokenMatches = System.Text.RegularExpressions.Regex.Matches(
            search,
            "\"[^\"]+\"|\\S+",
            System.Text.RegularExpressions.RegexOptions.None);

        foreach (System.Text.RegularExpressions.Match match in tokenMatches)
        {
            var token = match.Value;

            if (token.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                if (currentGroup.Count > 0)
                {
                    termGroups.Add(currentGroup);
                    currentGroup = new List<(string term, bool isNegated, bool isPhrase)>();
                }
                negate = false;
                continue;
            }

            if (token.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                negate = true;
                continue;
            }

            var isPhrase = token.Length >= 2 && token.StartsWith('"') && token.EndsWith('"');
            var term = isPhrase ? token[1..^1] : token.Trim('(', ')');

            if (!string.IsNullOrWhiteSpace(term))
            {
                currentGroup.Add((term, negate, isPhrase));
                negate = false;
            }
        }

        if (currentGroup.Count > 0)
        {
            termGroups.Add(currentGroup);
        }

        return termGroups;
    }

    /// <summary>
    /// Processes $expand to fetch related entities for each feature.
    /// </summary>
    private static async Task<Dictionary<long, Dictionary<string, object?[]>>> ProcessExpandAsync(
        string expand,
        LayerDefinition layer,
        long[] objectIds,
        IFeatureStore featureStore,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, Dictionary<string, object?[]>>();

        if (objectIds.Length == 0)
        {
            return result;
        }

        // Parse $expand expression - comma-separated list of relationship names
        var relationshipNames = expand
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find matching relationships
        foreach (var relationship in layer.LayerRelationships)
        {
            if (!relationshipNames.Contains(relationship.Name))
            {
                continue;
            }

            // Query related features
            var relatedQuery = RelatedQuery.ForObjects(objectIds, relationship);
            var relatedResult = await featureStore.QueryRelatedAsync(layer.Id, relatedQuery, cancellationToken);

            // Group related features by origin object ID
            foreach (var feature in relatedResult.Items)
            {
                // Try to get the origin key from the related feature's attributes
                if (!feature.Attributes.TryGetValue(relationship.DestinationForeignKeyField, out var originKeyValue))
                {
                    continue;
                }

                // Convert the origin key to long if possible
                long? originId = originKeyValue switch
                {
                    long l => l,
                    int i => i,
                    string s when long.TryParse(s, out var parsed) => parsed,
                    System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetInt64(),
                    _ => null
                };

                if (!originId.HasValue)
                {
                    continue;
                }

                if (!result.TryGetValue(originId.Value, out var relationsDict))
                {
                    relationsDict = new Dictionary<string, object?[]>();
                    result[originId.Value] = relationsDict;
                }

                var relatedFeatureDict = new Dictionary<string, object?>
                {
                    ["ObjectId"] = feature.Id,
                    ["Attributes"] = SerializeAttributes(feature.Attributes)
                };

                if (relationsDict.TryGetValue(relationship.Name, out var existingRelations))
                {
                    var newRelations = new object?[existingRelations.Length + 1];
                    Array.Copy(existingRelations, newRelations, existingRelations.Length);
                    newRelations[existingRelations.Length] = relatedFeatureDict;
                    relationsDict[relationship.Name] = newRelations;
                }
                else
                {
                    relationsDict[relationship.Name] = new object?[] { relatedFeatureDict };
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a PostgreSQL text search condition from parsed search terms.
    /// </summary>
    private static string BuildTextSearchCondition(
        List<List<(string term, bool isNegated, bool isPhrase)>> terms,
        LayerDefinition layer)
    {
        if (terms.Count == 0)
        {
            return "1=1"; // No search terms, match all
        }

        // Get text-searchable fields from the layer
        var textFields = layer.AttributeFields
            .Where(f => f.Type == FieldType.String)
            .Select(f => f.Name)
            .ToList();

        if (textFields.Count == 0)
        {
            return "1=0"; // No text fields to search
        }

        var groupConditions = new List<string>();

        foreach (var group in terms)
        {
            if (group.Count == 0)
            {
                continue;
            }

            var groupParts = new List<string>();

            foreach (var (term, isNegated, isPhrase) in group)
            {
                // Escape the term for SQL ILIKE
                var escapedTerm = term
                    .Replace("'", "''")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_");

                var fieldConditions = textFields
                    .Select(f => $"COALESCE(attributes->>'{f}', '') ILIKE '%{escapedTerm}%'")
                    .ToList();

                var condition = $"({string.Join(" OR ", fieldConditions)})";

                if (isNegated)
                {
                    condition = $"NOT {condition}";
                }

                groupParts.Add(condition);
            }

            if (groupParts.Count == 0)
            {
                continue;
            }

            var groupCondition = groupParts.Count == 1
                ? groupParts[0]
                : $"({string.Join(" AND ", groupParts)})";

            groupConditions.Add(groupCondition);
        }

        if (groupConditions.Count == 0)
        {
            return "1=1";
        }

        return string.Join(" OR ", groupConditions);
    }

    private static partial class Log
    {
        /// <summary>
        /// Logs when dynamic OData metadata generation fails and falls back to static metadata.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3000, Level = LogLevel.Warning, Message = "Failed to generate dynamic OData metadata, using static metadata.")]
        public static partial void MetadataFallback(ILogger logger, Exception exception);

        /// <summary>
        /// Logs when an invalid OData layers query is received.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="exception">The exception that occurred during query validation.</param>
        [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Invalid OData layers query.")]
        public static partial void InvalidLayersQuery(ILogger logger, Exception exception);

        /// <summary>
        /// Logs when an OData layers query fails to execute.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "OData layers query failed.")]
        public static partial void LayersQueryFailed(ILogger logger, Exception exception);

        /// <summary>
        /// Logs when an invalid OData features query is received for a specific layer.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="exception">The exception that occurred during query validation.</param>
        [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Invalid OData features query for layer {LayerId}.")]
        public static partial void InvalidFeaturesQuery(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when an OData features query fails to execute for a specific layer.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "OData features query failed for layer {LayerId}.")]
        public static partial void FeaturesQueryFailed(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when getting a specific feature fails via OData.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="objectId">The object identifier of the feature.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3005, Level = LogLevel.Error, Message = "OData get feature failed for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void GetFeatureFailed(ILogger logger, int layerId, long objectId, Exception exception);

        /// <summary>
        /// Logs when creating a feature fails via OData.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3006, Level = LogLevel.Error, Message = "OData create feature failed for layer {LayerId}.")]
        public static partial void CreateFeatureFailed(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when a feature to update is not found via OData.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="objectId">The object identifier of the feature that was not found.</param>
        /// <param name="exception">The exception that occurred.</param>
        [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "OData update feature not found for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void UpdateFeatureNotFound(ILogger logger, int layerId, long objectId, Exception exception);

        /// <summary>
        /// Logs when updating a feature fails via OData.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="objectId">The object identifier of the feature.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3008, Level = LogLevel.Error, Message = "OData update feature failed for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void UpdateFeatureFailed(ILogger logger, int layerId, long objectId, Exception exception);

        /// <summary>
        /// Logs when deleting a feature fails via OData.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="objectId">The object identifier of the feature.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3009, Level = LogLevel.Error, Message = "OData delete feature failed for layer {LayerId} and objectId {ObjectId}.")]
        public static partial void DeleteFeatureFailed(ILogger logger, int layerId, long objectId, Exception exception);

        /// <summary>
        /// Logs when parsing an OData batch request fails.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="exception">The exception that caused the parsing failure.</param>
        [LoggerMessage(EventId = 3010, Level = LogLevel.Warning, Message = "OData batch request parse failed.")]
        public static partial void BatchParseFailed(ILogger logger, Exception exception);

        /// <summary>
        /// Logs when an OData batch request fails to execute.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3011, Level = LogLevel.Error, Message = "OData batch request failed.")]
        public static partial void BatchFailed(ILogger logger, Exception exception);

        /// <summary>
        /// Logs when an invalid OData $apply expression is received.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="exception">The exception that occurred during expression validation.</param>
        [LoggerMessage(EventId = 3012, Level = LogLevel.Warning, Message = "Invalid OData $apply expression for layer {LayerId}.")]
        public static partial void InvalidApplyExpression(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when an OData $apply aggregation operation fails.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3013, Level = LogLevel.Error, Message = "OData $apply aggregation failed for layer {LayerId}.")]
        public static partial void ApplyFailed(ILogger logger, int layerId, Exception exception);

        /// <summary>
        /// Logs when an OData $search operation fails.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="layerId">The layer identifier.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        [LoggerMessage(EventId = 3014, Level = LogLevel.Error, Message = "OData $search failed for layer {LayerId}.")]
        public static partial void SearchFailed(ILogger logger, int layerId, Exception exception);
    }

    private static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }

    /// <summary>
    /// Generates the @odata.nextLink URL for pagination.
    /// </summary>
    private static string GenerateNextLink(HttpRequest request, int layerId, int nextSkip, int top, string? filter, string? select, string? orderby, bool? count)
    {
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var queryParams = new List<string>
        {
            $"$skip={nextSkip}",
            $"$top={top}"
        };

        if (!string.IsNullOrWhiteSpace(filter))
        {
            queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
        }

        if (!string.IsNullOrWhiteSpace(select))
        {
            queryParams.Add($"$select={Uri.EscapeDataString(select)}");
        }

        if (!string.IsNullOrWhiteSpace(orderby))
        {
            queryParams.Add($"$orderby={Uri.EscapeDataString(orderby)}");
        }

        if (count == true)
        {
            queryParams.Add("$count=true");
        }

        return $"{baseUrl}/odata/Features({layerId})?{string.Join("&", queryParams)}";
    }

    private static bool TryExtractSpatialFilter(string filter, out SpatialFilter? spatialFilter, out string? nonSpatialFilter, out string? error)
    {
        spatialFilter = null;
        nonSpatialFilter = filter;
        error = null;

        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        var trimmed = filter.Trim();

        if (TryParseODataSpatialFilter(trimmed, out var parsedSpatialFilter, out error))
        {
            spatialFilter = parsedSpatialFilter;
            nonSpatialFilter = null;
            return true;
        }

        var parts = System.Text.RegularExpressions.Regex.Split(trimmed, @"\s+and\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (parts.Length == 2)
        {
            if (TryParseODataSpatialFilter(parts[0].Trim(), out parsedSpatialFilter, out error))
            {
                spatialFilter = parsedSpatialFilter;
                nonSpatialFilter = parts[1].Trim();
                return true;
            }

            if (TryParseODataSpatialFilter(parts[1].Trim(), out parsedSpatialFilter, out error))
            {
                spatialFilter = parsedSpatialFilter;
                nonSpatialFilter = parts[0].Trim();
                return true;
            }
        }

        if (trimmed.Contains("geo.", StringComparison.OrdinalIgnoreCase))
        {
            error ??= "Unsupported spatial filter format.";
        }

        return false;
    }

    private static bool TryParseODataSpatialFilter(string filter, out SpatialFilter spatialFilter, out string? error)
    {
        spatialFilter = default;
        error = null;

        var intersectsMatch = System.Text.RegularExpressions.Regex.Match(
            filter,
            @"^geo\.intersects\(\s*(?<field>\w+)\s*,\s*geography'(?<wkt>[^']+)'\s*\)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (intersectsMatch.Success)
        {
            var field = intersectsMatch.Groups["field"].Value;
            if (!field.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                error = "Spatial filters are only supported on Geometry.";
                return false;
            }

            if (!TryCreateWkbFromWkt(intersectsMatch.Groups["wkt"].Value, out var geometryWkb, out error))
            {
                return false;
            }

            spatialFilter = SpatialFilter.Create(geometryWkb, SpatialRelationship.Intersects, 4326);
            return true;
        }

        var distanceMatch = System.Text.RegularExpressions.Regex.Match(
            filter,
            @"^geo\.distance\(\s*(?<field>\w+)\s*,\s*geography'(?<wkt>[^']+)'\s*\)\s*(?<op>lt|le|gt|ge|eq|ne)\s*(?<distance>-?\d+(?:\.\d+)?)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (distanceMatch.Success)
        {
            var field = distanceMatch.Groups["field"].Value;
            if (!field.Equals("geometry", StringComparison.OrdinalIgnoreCase))
            {
                error = "Spatial filters are only supported on Geometry.";
                return false;
            }

            if (!TryCreateWkbFromWkt(distanceMatch.Groups["wkt"].Value, out var geometryWkb, out error))
            {
                return false;
            }

            if (!double.TryParse(distanceMatch.Groups["distance"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var distanceValue) ||
                distanceValue <= 0)
            {
                error = "Distance must be a positive number.";
                return false;
            }

            var op = distanceMatch.Groups["op"].Value.ToLowerInvariant();
            var withinDistance = op is "lt" or "le" or "eq";

            spatialFilter = SpatialFilter.CreateDistanceFilter(
                geometryWkb,
                distanceValue,
                DistanceUnit.Meters,
                withinDistance,
                4326);

            return true;
        }

        if (filter.Contains("geo.", StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsupported spatial filter format.";
        }

        return false;
    }

    private static bool TryCreateWkbFromWkt(string wkt, out byte[] geometryWkb, out string? error)
    {
        geometryWkb = Array.Empty<byte>();
        error = null;

        try
        {
            var reader = new WKTReader();
            var geometry = reader.Read(wkt);
            if (geometry == null)
            {
                error = "Invalid spatial filter geometry.";
                return false;
            }

            if (geometry.SRID == 0)
            {
                geometry.SRID = 4326;
            }

            var (hasZ, hasM) = GetHasZandM(geometry);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: true, emitZ: hasZ, emitM: hasM);
            geometryWkb = writer.Write(geometry);
            return true;
        }
        catch
        {
            error = "Invalid spatial filter geometry.";
            return false;
        }
    }

    private static (bool hasZ, bool hasM) GetHasZandM(NetTopologySuite.Geometries.Geometry geometry)
    {
        if (geometry is GeometryCollection collection && collection.NumGeometries > 0)
        {
            return GetHasZandM(collection.GetGeometryN(0));
        }

        CoordinateSequence? sequence = geometry switch
        {
            Point point => point.CoordinateSequence,
            LineString lineString => lineString.CoordinateSequence,
            Polygon polygon => polygon.ExteriorRing.CoordinateSequence,
            MultiPoint multiPoint when multiPoint.NumGeometries > 0 => ((Point)multiPoint.GetGeometryN(0)).CoordinateSequence,
            MultiLineString multiLineString when multiLineString.NumGeometries > 0 => ((LineString)multiLineString.GetGeometryN(0)).CoordinateSequence,
            MultiPolygon multiPolygon when multiPolygon.NumGeometries > 0 => ((Polygon)multiPolygon.GetGeometryN(0)).ExteriorRing.CoordinateSequence,
            _ => null
        };

        if (sequence == null)
        {
            return (false, false);
        }

        return (HasOrdinateValues(sequence, Ordinate.Z), HasOrdinateValues(sequence, Ordinate.M));
    }

    private static bool HasOrdinateValues(CoordinateSequence sequence, Ordinate ordinate)
    {
        for (var i = 0; i < sequence.Count; i++)
        {
            var value = sequence.GetOrdinate(i, ordinate);
            if (!double.IsNaN(value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts basic OData $filter expressions to SQL WHERE clauses
    /// Supports: eq, ne, gt, lt, ge, le, contains, startswith, endswith, geo.distance, geo.intersects
    /// </summary>
    internal static (SqlFragment? sqlFragment, string? whereClause) ConvertODataFilterToSqlFragment(string? odataFilter)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
            return (null, null);

        var sql = odataFilter;
        var parameters = new List<object?>();
        var paramIndex = 0; // Start from 0 for @p0, @p1, etc.

        // Handle spatial functions first
        // geo.distance(Geometry, geography'POINT(x y)') lt/gt value
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"geo\.distance\(\s*(?<field>\w+)\s*,\s*geography'(?<geometry>[^']+)'\s*\)\s*(?<op>lt|gt|le|ge|eq|ne)\s*(?<distance>\d+(?:\.\d+)?)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var geometry = match.Groups["geometry"].Value;
                var op = match.Groups["op"].Value;
                var distance = match.Groups["distance"].Value;

                var fieldSql = MapODataField(field);
                var sqlOp = ConvertODataOperator(op);

                // Add parameters for geometry and distance
                var geometryParamIndex = paramIndex++;
                var distanceParamIndex = paramIndex++;
                parameters.Add(geometry);
                parameters.Add(double.Parse(distance));

                // Return parameterized SQL
                return $"ST_Distance({fieldSql}::geography, ST_GeomFromText(@p{geometryParamIndex})::geography) {sqlOp} @p{distanceParamIndex}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // geo.intersects(Geometry, geography'POLYGON(...)')
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"geo\.intersects\(\s*(?<field>\w+)\s*,\s*geography'(?<geometry>[^']+)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var geometry = match.Groups["geometry"].Value;

                var fieldSql = MapODataField(field);

                // Add parameter for geometry
                var geometryParamIndex = paramIndex++;
                parameters.Add(geometry);

                // Return parameterized SQL
                return $"ST_Intersects({fieldSql}, ST_GeomFromText(@p{geometryParamIndex}))";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Handle geo.distance(geometry, geography'POINT(lon lat)') comparisons
        // Example: geo.distance(geometry, geography'POINT(-122.4 37.8)') lt 1000
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"geo\.distance\s*\(\s*geometry\s*,\s*geography'POINT\s*\(\s*(?<lon>-?\d+\.?\d*)\s+(?<lat>-?\d+\.?\d*)\s*\)'\s*\)\s*(?<op>lt|le|gt|ge|eq|ne)\s+(?<dist>\d+\.?\d*)",
            match =>
            {
                var lon = match.Groups["lon"].Value;
                var lat = match.Groups["lat"].Value;
                var op = MapODataOperatorToSql(match.Groups["op"].Value);
                var dist = match.Groups["dist"].Value;
                // ST_Distance returns meters, OData geo.distance uses meters
                return $"ST_Distance(geometry::geography, ST_SetSRID(ST_MakePoint({lon}, {lat}), 4326)::geography) {op} {dist}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Handle geo.intersects(geometry, geography'POLYGON((...))')
        // Example: geo.intersects(geometry, geography'POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.9, -122.5 37.9, -122.5 37.7))')
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"geo\.intersects\s*\(\s*geometry\s*,\s*geography'(?<wkt>POLYGON\s*\([^)]+\)\s*)'?\s*\)",
            match =>
            {
                var wkt = match.Groups["wkt"].Value;
                // Convert to PostGIS ST_Intersects with ST_GeomFromText
                return $"ST_Intersects(geometry, ST_SetSRID(ST_GeomFromText('{wkt}'), 4326))";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Handle geo.intersects with POINT
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"geo\.intersects\s*\(\s*geometry\s*,\s*geography'(?<wkt>POINT\s*\([^)]+\))'?\s*\)",
            match =>
            {
                var wkt = match.Groups["wkt"].Value;
                return $"ST_Intersects(geometry, ST_SetSRID(ST_GeomFromText('{wkt}'), 4326))";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"contains\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var value = match.Groups["value"].Value;
                var fieldSql = MapODataField(field);

                // Add parameter for value
                var valueParamIndex = paramIndex++;
                parameters.Add($"%{value}%");

                return $"{fieldSql} LIKE @p{valueParamIndex}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"startswith\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var value = match.Groups["value"].Value;
                var fieldSql = MapODataField(field);

                // Add parameter for value
                var valueParamIndex = paramIndex++;
                parameters.Add($"{value}%");

                return $"{fieldSql} LIKE @p{valueParamIndex}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"endswith\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var value = match.Groups["value"].Value;
                var fieldSql = MapODataField(field);

                // Add parameter for value
                var valueParamIndex = paramIndex++;
                parameters.Add($"%{value}");

                return $"{fieldSql} LIKE @p{valueParamIndex}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        sql = sql
            .Replace(" eq ", " = ", StringComparison.OrdinalIgnoreCase)
            .Replace(" ne ", " <> ", StringComparison.OrdinalIgnoreCase)
            .Replace(" gt ", " > ", StringComparison.OrdinalIgnoreCase)
            .Replace(" lt ", " < ", StringComparison.OrdinalIgnoreCase)
            .Replace(" ge ", " >= ", StringComparison.OrdinalIgnoreCase)
            .Replace(" le ", " <= ", StringComparison.OrdinalIgnoreCase)
            .Replace(" and ", " AND ", StringComparison.OrdinalIgnoreCase)
            .Replace(" or ", " OR ", StringComparison.OrdinalIgnoreCase);

        // Convert OData field references to JSONB queries with parameterization
        // Example: name eq 'value' -> attributes->>'name' = $n
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"\b(?<field>\w+)\s*(?<op>=|<>|>|<|>=|<=)\s*(?<value>('([^']*)')|(-?\d+(?:\.\d+)?)|true|false|null)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var op = match.Groups["op"].Value;
                var value = match.Groups["value"].Value;
                var fieldLower = field.Trim().ToLowerInvariant();
                var isCoreField = fieldLower == "objectid" || fieldLower == "layerid";

                var fieldSql = MapODataField(field);
                var valueLower = value.ToLowerInvariant();

                if (valueLower == "null")
                {
                    return op == "<>"
                        ? $"{fieldSql} IS NOT NULL"
                        : $"{fieldSql} IS NULL";
                }

                var valueParamIndex = paramIndex++;

                if (valueLower is "true" or "false")
                {
                    var castedField = isCoreField ? fieldSql : $"({fieldSql})::boolean";
                    parameters.Add(bool.Parse(valueLower));
                    return $"{castedField} {op} @p{valueParamIndex}";
                }

                if (value.StartsWith('\'') && value.EndsWith('\''))
                {
                    parameters.Add(value.Substring(1, value.Length - 2));
                    return $"{fieldSql} {op} @p{valueParamIndex}";
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numValue))
                {
                    var castedField = isCoreField ? fieldSql : $"({fieldSql})::double precision";
                    parameters.Add(numValue);
                    return $"{castedField} {op} @p{valueParamIndex}";
                }

                parameters.Add(value);
                return $"{fieldSql} {op} @p{valueParamIndex}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // If we have parameters, return SqlFragment; otherwise fallback to string
        if (parameters.Count > 0)
        {
            return (new SqlFragment(sql, parameters), null);
        }

        return (null, sql);
    }

    private static string MapODataField(string field)
    {
        var fieldName = field.Trim();
        var fieldLower = fieldName.ToLowerInvariant();

        if (fieldLower == "objectid")
        {
            return "objectid";
        }

        if (fieldLower == "layerid")
        {
            return "layer_id";
        }

        if (fieldLower == "geometry")
        {
            return "geometry";
        }

        return $"attributes->>'{fieldName}'";
    }

    private static string ConvertODataOperator(string odataOp)
    {
        return odataOp.ToLowerInvariant() switch
        {
            "eq" => "=",
            "ne" => "<>",
            "gt" => ">",
            "ge" => ">=",
            "lt" => "<",
            "le" => "<=",
            _ => odataOp
        };
    }

    /// <summary>
    /// Maps OData comparison operators to SQL operators
    /// </summary>
    private static string MapODataOperatorToSql(string op)
    {
        return op.ToLowerInvariant() switch
        {
            "eq" => "=",
            "ne" => "<>",
            "gt" => ">",
            "lt" => "<",
            "ge" => ">=",
            "le" => "<=",
            _ => throw new ArgumentException($"Unknown OData operator: {op}")
        };
    }

    /// <summary>
    /// Parses OData $orderby expression into OrderByClause array.
    /// Format: "field1 asc, field2 desc" or "field1, field2 desc"
    /// Default direction is ascending when not specified.
    /// </summary>
    private static ImmutableArray<OrderByClause>? ParseODataOrderBy(string? orderby, Honua.Core.Features.Catalog.Domain.LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(orderby))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        var parts = orderby.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            var fieldName = tokens[0].Trim();

            // Validate field name (alphanumeric and underscores only)
            if (!System.Text.RegularExpressions.Regex.IsMatch(fieldName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            {
                throw new ArgumentException($"Invalid field name in $orderby: {fieldName}");
            }

            // Default to ascending, check for explicit direction
            var ascending = true;
            if (tokens.Length > 1)
            {
                var direction = tokens[1].Trim().ToLowerInvariant();
                if (direction == "desc")
                {
                    ascending = false;
                }
                else if (direction != "asc")
                {
                    throw new ArgumentException($"Invalid sort direction in $orderby: {direction}. Use 'asc' or 'desc'.");
                }
            }

            var fieldDefinition = layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            var resolvedField = fieldDefinition?.Name ?? fieldName;
            var fieldType = fieldDefinition?.Type;

            clauses.Add(new OrderByClause(resolvedField, ascending, fieldType));
        }

        return clauses.Count > 0 ? clauses.ToImmutableArray() : null;
    }

    /// <summary>
    /// Applies basic filtering to layer collections
    /// </summary>
    private static IEnumerable<Honua.Core.Features.Catalog.Domain.LayerDefinition> ApplyBasicFilter(
        IEnumerable<Honua.Core.Features.Catalog.Domain.LayerDefinition> layers,
        string filter)
    {
        // Simple name filtering - production would use a proper OData expression parser
        if (filter.Contains("name", StringComparison.OrdinalIgnoreCase))
        {
            var nameMatch = System.Text.RegularExpressions.Regex.Match(
                filter,
                @"name\s+eq\s+'([^']*)'",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (nameMatch.Success)
            {
                var nameValue = nameMatch.Groups[1].Value;
                return layers.Where(l => string.Equals(l.Name, nameValue, StringComparison.OrdinalIgnoreCase));
            }
        }

        return layers;
    }

    /// <summary>
    /// Applies field selection to result objects (AOT-compatible approach)
    /// </summary>
    private static object[] ApplyFieldSelection(Dictionary<string, object?>[] data, string select)
    {
        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return data.Select(item =>
        {
            var dict = new Dictionary<string, object?>();

            if (item is IDictionary<string, object?> existingDict)
            {
                // If it's already a dictionary, filter based on selected fields
                foreach (var kvp in existingDict)
                {
                    if (fields.Contains(kvp.Key))
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }
            }

            return dict;
        }).ToArray();
    }

    private static string SerializeAttributes(IReadOnlyDictionary<string, object?> attributes)
    {
        var normalized = attributes.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        return JsonSerializer.Serialize(normalized, ODataJsonContext.Default.DictionaryStringObject);
    }

    private static object? NormalizeODataValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return ConvertJsonElement(element);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            return readOnlyDict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is IDictionary<string, object?> dict)
        {
            return dict.ToDictionary(kvp => kvp.Key, kvp => NormalizeODataValue(kvp.Value));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
            {
                list.Add(NormalizeODataValue(item));
            }

            return list.ToArray();
        }

        return value;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                element.TryGetDouble(out var doubleVal) ? doubleVal :
                element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => ConvertJsonElement(prop.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            _ => element.GetRawText()
        };
    }
}
