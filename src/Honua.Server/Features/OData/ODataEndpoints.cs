// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.OData.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

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

        return Results.Json(serviceDocument, ODataJsonContext.Default.ServiceDocument, contentType: "application/json;odata.metadata=minimal");
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
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureQueryValidator queryValidator,
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
                return TypedResults.BadRequest("$top must be a positive integer.");
            }

            if (skip.HasValue && skip.Value < 0)
            {
                return TypedResults.BadRequest("$skip must be a non-negative integer.");
            }

            var validationResult = queryValidator.ValidateQueryLimits(new QueryParameters
            {
                ResultRecordCount = top,
                ResultOffset = skip
            });

            if (!validationResult.IsValid)
            {
                return TypedResults.BadRequest($"Invalid OData query: {validationResult.ErrorMessage}");
            }

            var validatedParams = validationResult.ValidatedParameters!;

            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var layerEnumerable = layers.AsEnumerable();

            // Apply basic filtering if specified
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerEnumerable = ApplyBasicFilter(layerEnumerable, filter);
            }

            var filteredLayers = layerEnumerable.ToList();
            long? totalCount = count == true ? filteredLayers.Count : null;

            // Apply skip/top pagination
            if (validatedParams.ResultOffset.HasValue)
            {
                filteredLayers = filteredLayers.Skip(validatedParams.ResultOffset.Value).ToList();
            }

            if (validatedParams.ResultRecordCount.HasValue)
            {
                filteredLayers = filteredLayers.Take(validatedParams.ResultRecordCount.Value).ToList();
            }

            var layerData = filteredLayers.Select(l => new Dictionary<string, object?>
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

            return Results.Json(response, ODataJsonContext.Default.ODataResponse, contentType: "application/json;odata.metadata=minimal");
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
        HttpContext context,
        int layerId,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        IFeatureQueryValidator queryValidator,
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
                return TypedResults.BadRequest("$top must be a positive integer.");
            }

            if (skip.HasValue && skip.Value < 0)
            {
                return TypedResults.BadRequest("$skip must be a non-negative integer.");
            }

            var validationResult = queryValidator.ValidateQueryLimits(new QueryParameters
            {
                ResultRecordCount = top,
                ResultOffset = skip
            });

            if (!validationResult.IsValid)
            {
                return TypedResults.BadRequest($"Invalid OData query: {validationResult.ErrorMessage}");
            }

            var validatedParams = validationResult.ValidatedParameters!;

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
                Limit = validatedParams.ResultRecordCount,
                Offset = validatedParams.ResultOffset
            };

            // Execute query
            var queryResult = await featureStore.QueryAsync(layerId, featureQuery, cancellationToken);

            // Convert features to OData format
            var featuresData = queryResult.Items.Select(f => new Dictionary<string, object?>
            {
                ["ObjectId"] = f.Id,
                ["LayerId"] = layerId,
                ["Geometry"] = f.Geometry != null ? Convert.ToBase64String(f.Geometry) : null,
                ["Attributes"] = SerializeAttributes(f.Attributes)
            }).ToArray();

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? featuresData.Cast<object>().ToArray()
                : ApplyFieldSelection(featuresData, select);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var response = new ODataResponse
            {
                Context = $"{baseUrl}/odata/$metadata#Features",
                Count = count == true ? queryResult.TotalCount : null,
                Value = result
            };

            return Results.Json(response, ODataJsonContext.Default.ODataResponse, contentType: "application/json;odata.metadata=minimal");
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
        var sql = odataFilter;

        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"contains\(\s*(?<field>\w+)\s*,\s*'(?<value>[^']*)'\s*\)",
            match =>
            {
                var field = match.Groups["field"].Value;
                var value = match.Groups["value"].Value;
                var fieldSql = MapODataField(field);
                return $"{fieldSql} LIKE '%{value}%'";
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
                return $"{fieldSql} LIKE '{value}%'";
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
                return $"{fieldSql} LIKE '%{value}'";
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

        // Convert OData field references to JSONB queries
        // Example: name eq 'value' -> attributes->>'name' = 'value'
        sql = System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"\b(?<field>\w+)\s*(?<op>=|<>|>|<|>=|<=)\s*(?<value>('([^']*)')|(-?\d+(?:\.\d+)?))",
            match =>
            {
                var field = match.Groups["field"].Value;
                var op = match.Groups["op"].Value;
                var value = match.Groups["value"].Value;
                var isNumericValue = !value.StartsWith('\'');
                var fieldLower = field.Trim().ToLowerInvariant();
                var isCoreField = fieldLower == "objectid" || fieldLower == "layerid";

                if (isNumericValue && !isCoreField)
                {
                    value = $"'{value}'";
                }

                var fieldSql = MapODataField(field);

                return $"{fieldSql} {op} {value}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return sql;
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

        return $"attributes->>'{fieldName}'";
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
