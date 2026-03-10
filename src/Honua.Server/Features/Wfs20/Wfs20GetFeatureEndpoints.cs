// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Wfs20.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// WFS 2.0 GetFeature and GetPropertyValue endpoints
/// </summary>
internal static class Wfs20GetFeatureEndpoints
{
    private static readonly string[] GetPostMethods = { "GET", "POST" };
    /// <summary>
    /// Maps WFS 2.0 GetFeature endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapWfs20GetFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GetFeature endpoint
        var getFeature = endpoints.MapMethods("/wfs", GetPostMethods, HandleGetFeature)
            .WithDisplayName("WFS 2.0 GetFeature")
            .WithName("Wfs20GetFeature")
            .WithSummary("Get features from WFS 2.0 service")
            .WithDescription("Retrieve features using WFS 2.0 GetFeature operation with filtering, paging, and format options")
            .WithTags("WFS 2.0")
            .Produces<WfsFeatures>(200, "application/xml")
            .Produces<string>(200, "application/geo+json")
            .Produces<string>(200, "text/csv")
            .Produces<ExceptionReport>(400, "application/xml")
            .Produces(404);

        // GetPropertyValue endpoint
        var getPropertyValue = endpoints.MapMethods("/wfs", GetPostMethods, HandleGetPropertyValue)
            .WithDisplayName("WFS 2.0 GetPropertyValue")
            .WithName("Wfs20GetPropertyValue")
            .WithSummary("Get property values from WFS 2.0 service")
            .WithDescription("Retrieve specific property values using WFS 2.0 GetPropertyValue operation")
            .WithTags("WFS 2.0")
            .Produces<string>(200, "application/xml")
            .Produces<ExceptionReport>(400, "application/xml")
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles WFS 2.0 GetFeature requests
    /// </summary>
    private static async Task<IResult> HandleGetFeature(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        string? typeNames,
        string? outputFormat,
        string? count,
        string? startIndex,
        string? sortBy,
        string? filter,
        string? bbox,
        string? resourceId,
        string? propertyName,
        string? srsName,
        [FromServices] ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        // Check if this is a GetFeature request
        var requestParam = request ?? context.Request.Query[Wfs20Utilities.ParameterNames.Request].FirstOrDefault();
        if (!string.Equals(requestParam, Wfs20Utilities.Operations.GetFeature, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Not a GetFeature request");
        }

        Wfs20Log.GetFeatureRequested(logger, typeNames ?? "unknown", outputFormat ?? "default");

        // Validate query parameters
        var validationError = Wfs20Utilities.ValidateRequestParameters(
            context.Request.Query,
            Wfs20Utilities.AllowedQueryParameters.GetFeature);

        if (validationError is not null)
        {
            return CreateExceptionResponse("InvalidParameterValue", validationError, null);
        }

        // Parse and validate parameters
        var parsedTypeNames = Wfs20Utilities.ParseTypeNames(typeNames);
        if (parsedTypeNames.Length == 0)
        {
            return CreateExceptionResponse("MissingParameterValue", "TypeNames parameter is required", "typeNames");
        }

        var parsedOutputFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
        var parsedCount = Wfs20Utilities.ParseCount(count);
        var parsedStartIndex = Wfs20Utilities.ParseStartIndex(startIndex);

        try
        {
            // TODO: Implement GetFeature logic
            // This would involve:
            // 1. Validate feature types exist
            // 2. Parse filter (if provided) using FES 2.0 parser
            // 3. Query the feature store
            // 4. Transform results to requested CRS (if needed)
            // 5. Format output according to outputFormat
            // 6. Apply paging (count/startIndex)

            var response = await ProcessGetFeatureRequest(
                parsedTypeNames,
                parsedOutputFormat,
                parsedCount,
                parsedStartIndex,
                sortBy,
                filter,
                bbox,
                resourceId,
                propertyName,
                srsName);

            Wfs20Log.GetFeatureReturned(logger, 0, 0);
            return Results.Content(response.Content, response.ContentType);
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(logger, "GetFeature", ex.Message);
            return CreateExceptionResponse("NoApplicableCode", $"Internal server error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Handles WFS 2.0 GetPropertyValue requests
    /// </summary>
    private static async Task<IResult> HandleGetPropertyValue(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        string? typeNames,
        string? propertyName,
        string? count,
        string? startIndex,
        string? filter,
        string? bbox,
        string? resourceId,
        string? srsName,
        [FromServices] ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        // Check if this is a GetPropertyValue request
        var requestParam = request ?? context.Request.Query[Wfs20Utilities.ParameterNames.Request].FirstOrDefault();
        if (!string.Equals(requestParam, Wfs20Utilities.Operations.GetPropertyValue, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Not a GetPropertyValue request");
        }

        Wfs20Log.GetPropertyValueRequested(logger, propertyName ?? "unknown", typeNames ?? "unknown");

        // Validate query parameters
        var validationError = Wfs20Utilities.ValidateRequestParameters(
            context.Request.Query,
            Wfs20Utilities.AllowedQueryParameters.GetPropertyValue);

        if (validationError is not null)
        {
            return CreateExceptionResponse("InvalidParameterValue", validationError, null);
        }

        // Parse and validate parameters
        var parsedTypeNames = Wfs20Utilities.ParseTypeNames(typeNames);
        if (parsedTypeNames.Length == 0)
        {
            return CreateExceptionResponse("MissingParameterValue", "TypeNames parameter is required", "typeNames");
        }

        if (string.IsNullOrEmpty(propertyName))
        {
            return CreateExceptionResponse("MissingParameterValue", "PropertyName parameter is required", "propertyName");
        }

        var parsedCount = Wfs20Utilities.ParseCount(count);
        var parsedStartIndex = Wfs20Utilities.ParseStartIndex(startIndex);

        try
        {
            // TODO: Implement GetPropertyValue logic
            // This would involve:
            // 1. Validate feature type exists
            // 2. Validate property exists on feature type
            // 3. Parse filter (if provided) using FES 2.0 parser
            // 4. Query the feature store for specific property values
            // 5. Apply paging (count/startIndex)
            // 6. Format as XML response

            var response = await ProcessGetPropertyValueRequest(
                parsedTypeNames[0],
                propertyName,
                parsedCount,
                parsedStartIndex,
                filter,
                bbox,
                resourceId,
                srsName);

            Wfs20Log.GetPropertyValueReturned(logger, 0);
            return Results.Content(response, "application/xml");
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(logger, "GetPropertyValue", ex.Message);
            return CreateExceptionResponse("NoApplicableCode", $"Internal server error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Processes GetFeature request and returns formatted response
    /// </summary>
    private static async Task<(string Content, string ContentType)> ProcessGetFeatureRequest(
        string[] typeNames,
        string outputFormat,
        int count,
        int startIndex,
        string? sortBy,
        string? filter,
        string? bbox,
        string? resourceId,
        string? propertyName,
        string? srsName)
    {
        // TODO: Implement actual feature querying
        // This is a placeholder implementation

        await Task.Delay(1); // Placeholder for async operation

        return outputFormat switch
        {
            var fmt when fmt == Wfs20Utilities.OutputFormats.Gml32 =>
                (CreateEmptyGmlFeatureCollection(), "application/gml+xml; version=3.2"),
            var fmt when fmt == Wfs20Utilities.OutputFormats.GeoJson =>
                (CreateEmptyGeoJsonFeatureCollection(), "application/geo+json"),
            var fmt when fmt == Wfs20Utilities.OutputFormats.Csv =>
                (CreateEmptyCsvResponse(), "text/csv"),
            _ => (CreateEmptyGmlFeatureCollection(), "application/gml+xml; version=3.2")
        };
    }

    /// <summary>
    /// Processes GetPropertyValue request and returns XML response
    /// </summary>
    private static async Task<string> ProcessGetPropertyValueRequest(
        string typeName,
        string propertyName,
        int count,
        int startIndex,
        string? filter,
        string? bbox,
        string? resourceId,
        string? srsName)
    {
        // TODO: Implement actual property value querying
        // This is a placeholder implementation

        await Task.Delay(1); // Placeholder for async operation

        return CreateEmptyPropertyValueResponse();
    }

    /// <summary>
    /// Creates empty GML feature collection for placeholder
    /// </summary>
    private static string CreateEmptyGmlFeatureCollection()
    {
        return """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:FeatureCollection
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                numberMatched="0"
                numberReturned="0"
                timeStamp="2026-03-10T00:00:00Z">
                <gml:boundedBy>
                    <gml:Null>unknown</gml:Null>
                </gml:boundedBy>
            </wfs:FeatureCollection>
            """;
    }

    /// <summary>
    /// Creates empty GeoJSON feature collection for placeholder
    /// </summary>
    private static string CreateEmptyGeoJsonFeatureCollection()
    {
        return """
            {
                "type": "FeatureCollection",
                "features": [],
                "crs": {
                    "type": "name",
                    "properties": {
                        "name": "EPSG:4326"
                    }
                }
            }
            """;
    }

    /// <summary>
    /// Creates empty CSV response for placeholder
    /// </summary>
    private static string CreateEmptyCsvResponse()
    {
        return "# No features found\n";
    }

    /// <summary>
    /// Creates empty property value response for placeholder
    /// </summary>
    private static string CreateEmptyPropertyValueResponse()
    {
        return """
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:ValueCollection
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                numberMatched="0"
                numberReturned="0"
                timeStamp="2026-03-10T00:00:00Z">
            </wfs:ValueCollection>
            """;
    }

    /// <summary>
    /// Creates a WFS exception response
    /// </summary>
    private static IResult CreateExceptionResponse(string exceptionCode, string exceptionText, string? locator)
    {
        var exceptionReport = new ExceptionReport
        {
            Exceptions = new[]
            {
                new ExceptionType
                {
                    ExceptionCode = exceptionCode,
                    ExceptionText = exceptionText,
                    Locator = locator
                }
            }
        };

        // TODO: Serialize to XML properly
        var xmlContent = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1" version="1.1.0">
                <ows:Exception exceptionCode="{exceptionCode}" {(locator != null ? $"locator=\"{locator}\"" : "")}>
                    {exceptionText}
                </ows:Exception>
            </ows:ExceptionReport>
            """;

        return Results.BadRequest(Results.Content(xmlContent, "application/xml"));
    }
}
