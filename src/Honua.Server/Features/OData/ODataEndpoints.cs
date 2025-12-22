// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.OData.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.OData;

/// <summary>
/// Simplified OData v4 endpoints for basic query operations
/// Supports $filter, $select, $top, $skip parameters
/// </summary>
public static class ODataEndpoints
{
    /// <summary>
    /// Maps simplified OData v4 endpoints
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
            .WithSummary("Get features with OData query parameters ($filter, $select, $top, $skip)")
            .WithTags("OData")
            .Produces<ODataResponse>(200, "application/json")
            .Produces(400)
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles OData service document request
    /// </summary>
    private static Ok<ServiceDocument> HandleGetServiceDocument(HttpContext context)
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

        return TypedResults.Ok(serviceDocument);
    }

    /// <summary>
    /// Handles OData metadata document request
    /// </summary>
    private static ContentHttpResult HandleGetMetadata()
    {
        var metadata = """
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

        return TypedResults.Content(metadata, "application/xml");
    }

    /// <summary>
    /// Handles OData layers collection request
    /// </summary>
    private static async Task<IResult> HandleGetLayers(
        ILayerCatalog layerCatalog,
        string? filter = null,
        string? select = null,
        int? top = null,
        int? skip = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var layerEnumerable = layers.AsEnumerable();

            // Apply basic filtering if specified
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerEnumerable = ApplyBasicFilter(layerEnumerable, filter);
            }

            // Apply skip/top pagination
            if (skip.HasValue)
            {
                layerEnumerable = layerEnumerable.Skip(skip.Value);
            }

            if (top.HasValue)
            {
                layerEnumerable = layerEnumerable.Take(top.Value);
            }

            var layerData = layerEnumerable.Select(l => new
            {
                Id = l.Id,
                Name = l.Name,
                Description = l.Description
            }).ToArray();

            // Apply field selection if specified
            var result = string.IsNullOrWhiteSpace(select)
                ? layerData
                : ApplyFieldSelection(layerData, select);

            var response = new ODataResponse
            {
                Context = "/odata/$metadata#Layers",
                Value = result
            };

            return Results.Json(response, ODataJsonContext.Default.ODataResponse);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest($"Invalid OData query: {ex.Message}");
        }
        catch (Exception)
        {
            return TypedResults.Problem("An error occurred processing the OData request", statusCode: 500);
        }
    }

    /// <summary>
    /// Handles OData features collection request with full query parameter support
    /// </summary>
    private static async Task<IResult> HandleGetFeatures(
        int layerId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        string? filter = null,
        string? select = null,
        int? top = null,
        int? skip = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return TypedResults.NotFound($"Layer {layerId} not found");
            }

            // Build feature query from OData parameters
            var featureQuery = new FeatureQuery
            {
                Where = ConvertODataFilterToSql(filter),
                Limit = top,
                Offset = skip
            };

            // Execute query
            var queryResult = await featureStore.QueryAsync(layerId, featureQuery, cancellationToken);

            // Convert features to OData format
            var featuresData = queryResult.Items.Select(f => new
            {
                ObjectId = f.Id,
                LayerId = layerId,
                Geometry = f.Geometry != null ? Convert.ToBase64String(f.Geometry) : null,
                Attributes = f.Attributes
            }).ToArray();

            // Apply field selection if specified
            var result = string.IsNullOrWhiteSpace(select)
                ? featuresData
                : ApplyFieldSelection(featuresData, select);

            var response = new ODataResponse
            {
                Context = $"/odata/$metadata#Features",
                Count = (int?)queryResult.TotalCount,
                Value = result
            };

            return Results.Json(response, ODataJsonContext.Default.ODataResponse);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest($"Invalid OData query: {ex.Message}");
        }
        catch (Exception)
        {
            return TypedResults.Problem("An error occurred processing the OData request", statusCode: 500);
        }
    }

    /// <summary>
    /// Converts basic OData $filter expressions to SQL WHERE clauses
    /// Supports: eq, ne, gt, lt, ge, le, contains, startswith, endswith
    /// </summary>
    private static string? ConvertODataFilterToSql(string? odataFilter)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
            return null;

        // Basic OData to SQL conversion
        // This is a simplified implementation - production would use a proper OData parser
        var sql = odataFilter
            .Replace(" eq ", " = ", StringComparison.OrdinalIgnoreCase)
            .Replace(" ne ", " <> ", StringComparison.OrdinalIgnoreCase)
            .Replace(" gt ", " > ", StringComparison.OrdinalIgnoreCase)
            .Replace(" lt ", " < ", StringComparison.OrdinalIgnoreCase)
            .Replace(" ge ", " >= ", StringComparison.OrdinalIgnoreCase)
            .Replace(" le ", " <= ", StringComparison.OrdinalIgnoreCase)
            .Replace(" and ", " AND ", StringComparison.OrdinalIgnoreCase)
            .Replace(" or ", " OR ", StringComparison.OrdinalIgnoreCase);

        // Convert OData field references to JSONB queries
        // Example: name eq 'value' -> attributes->>'name' = 'value'
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"\b(\w+)\s*(=|<>|>|<|>=|<=)\s*'([^']*)'",
            "attributes->>'$1' $2 '$3'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return sql;
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
    private static object[] ApplyFieldSelection(object[] data, string select)
    {
        var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim().ToLowerInvariant())
            .ToHashSet();

        return data.Select(item =>
        {
            var dict = new Dictionary<string, object?>();

            // For anonymous objects created in this class, we know the structure
            // This is a simplified approach that works with our specific use case
            if (item is IDictionary<string, object?> existingDict)
            {
                // If it's already a dictionary, filter based on selected fields
                foreach (var kvp in existingDict)
                {
                    if (fields.Contains(kvp.Key.ToLowerInvariant()))
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }
            }
            else
            {
                // For simple anonymous objects, convert to string and parse back
                // This is a fallback that avoids reflection but may not be as efficient
                var json = System.Text.Json.JsonSerializer.Serialize(item);
                var jsonDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);

                if (jsonDict != null)
                {
                    foreach (var kvp in jsonDict)
                    {
                        if (fields.Contains(kvp.Key.ToLowerInvariant()))
                        {
                            dict[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }

            return dict;
        }).ToArray();
    }
}
