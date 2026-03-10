// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Wfs20.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// WFS 2.0 DescribeFeatureType endpoints
/// </summary>
internal static class Wfs20DescribeFeatureTypeEndpoints
{
    private static readonly string[] GetPostMethods = { "GET", "POST" };
    /// <summary>
    /// Maps WFS 2.0 DescribeFeatureType endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapWfs20DescribeFeatureTypeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var describeFeatureType = endpoints.MapMethods("/wfs", GetPostMethods, HandleDescribeFeatureType)
            .WithDisplayName("WFS 2.0 DescribeFeatureType")
            .WithName("Wfs20DescribeFeatureType")
            .WithSummary("Describe feature types in WFS 2.0 service")
            .WithDescription("Get XML schema definitions for feature types using WFS 2.0 DescribeFeatureType operation")
            .WithTags("WFS 2.0")
            .Produces<string>(200, "application/xml")
            .Produces<ExceptionReport>(400, "application/xml")
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles WFS 2.0 DescribeFeatureType requests
    /// </summary>
    private static async Task<IResult> HandleDescribeFeatureType(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        string? typeNames,
        string? outputFormat,
        [FromServices] ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        // Check if this is a DescribeFeatureType request
        var requestParam = request ?? context.Request.Query[Wfs20Utilities.ParameterNames.Request].FirstOrDefault();
        if (!string.Equals(requestParam, Wfs20Utilities.Operations.DescribeFeatureType, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Not a DescribeFeatureType request");
        }

        Wfs20Log.DescribeFeatureTypeRequested(logger, typeNames ?? "all");

        // Validate query parameters
        var validationError = Wfs20Utilities.ValidateRequestParameters(
            context.Request.Query,
            Wfs20Utilities.AllowedQueryParameters.DescribeFeatureType);

        if (validationError is not null)
        {
            return CreateExceptionResponse("InvalidParameterValue", validationError, null);
        }

        // Parse parameters
        var parsedTypeNames = Wfs20Utilities.ParseTypeNames(typeNames);
        var normalizedOutputFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat) ?? "application/xml";

        try
        {
            // TODO: Implement DescribeFeatureType logic
            // This would involve:
            // 1. Get feature type definitions from layer catalog
            // 2. Generate XML schema for each feature type
            // 3. Handle case where no typeNames specified (return all)
            // 4. Validate requested feature types exist

            var schemaResponse = await GenerateFeatureTypeSchemas(parsedTypeNames);

            Wfs20Log.DescribeFeatureTypeReturned(logger, parsedTypeNames.Length > 0 ? parsedTypeNames.Length : 1);
            return Results.Content(schemaResponse, "application/xml");
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(logger, "DescribeFeatureType", ex.Message);
            return CreateExceptionResponse("NoApplicableCode", $"Internal server error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Generates XML schema definitions for the requested feature types
    /// </summary>
    private static async Task<string> GenerateFeatureTypeSchemas(string[] typeNames)
    {
        // TODO: Implement actual schema generation
        // This would involve:
        // 1. Query layer catalog for feature type definitions
        // 2. Generate XML Schema (XSD) for each feature type
        // 3. Include geometry and attribute definitions
        // 4. Handle inheritance and complex types

        await Task.Delay(1); // Placeholder for async operation

        return CreatePlaceholderSchema(typeNames);
    }

    /// <summary>
    /// Creates a placeholder XML schema response
    /// </summary>
    private static string CreatePlaceholderSchema(string[] typeNames)
    {
        var targetNamespace = "http://honua.io/wfs";
        var schemaContent = typeNames.Length > 0
            ? string.Join("\n", typeNames.Select(CreateFeatureTypeSchema))
            : CreateFeatureTypeSchema("placeholder_feature");

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xs:schema
                xmlns:xs="http://www.w3.org/2001/XMLSchema"
                xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="{targetNamespace}"
                targetNamespace="{targetNamespace}"
                elementFormDefault="qualified"
                attributeFormDefault="unqualified">

                <xs:import namespace="http://www.opengis.net/gml/3.2" schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>

                {schemaContent}

            </xs:schema>
            """;
    }

    /// <summary>
    /// Creates a schema definition for a single feature type
    /// </summary>
    private static string CreateFeatureTypeSchema(string typeName)
    {
        return $"""
            <!-- Feature type: {typeName} -->
            <xs:element name="{typeName}" type="honua:{typeName}Type" substitutionGroup="gml:AbstractFeature"/>

            <xs:complexType name="{typeName}Type">
                <xs:complexContent>
                    <xs:extension base="gml:AbstractFeatureType">
                        <xs:sequence>
                            <xs:element name="geometry" type="gml:GeometryPropertyType" minOccurs="0"/>
                            <xs:element name="id" type="xs:string"/>
                            <xs:element name="name" type="xs:string" minOccurs="0"/>
                            <xs:element name="description" type="xs:string" minOccurs="0"/>
                        </xs:sequence>
                    </xs:extension>
                </xs:complexContent>
            </xs:complexType>
            """;
    }

    /// <summary>
    /// Creates a WFS exception response
    /// </summary>
    private static IResult CreateExceptionResponse(string exceptionCode, string exceptionText, string? locator)
    {
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