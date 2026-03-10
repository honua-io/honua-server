// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Wfs20.Models;
using Honua.Server.Features.Wfs20.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// Core WFS 2.0 endpoints (GetCapabilities)
/// </summary>
internal static class Wfs20CoreEndpoints
{
    private static readonly TimeSpan _capabilitiesCacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Maps core WFS 2.0 endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapWfs20CoreEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // WFS 2.0 GetCapabilities endpoint - constrained to request=GetCapabilities
        var getCapabilities = endpoints.MapGet("/wfs", HandleGetCapabilities)
            .WithDisplayName("WFS 2.0 GetCapabilities")
            .WithName("Wfs20GetCapabilities")
            .WithSummary("Get WFS 2.0 capabilities document")
            .WithDescription("Returns the WFS 2.0 capabilities document with service metadata, available operations, and feature types")
            .WithTags("WFS 2.0")
            .CacheOutput("Wfs20Capabilities")
            .Produces<WfsCapabilities>(200, "application/xml")
            .Produces<ExceptionReport>(400, "application/xml")
            .Produces(404);

        // Alternative endpoint without query parameters for convenience
        var getCapabilitiesAlt = endpoints.MapGet("/wfs/capabilities", HandleGetCapabilitiesAlt)
            .WithDisplayName("WFS 2.0 GetCapabilities (Alternative)")
            .WithName("Wfs20GetCapabilitiesAlt")
            .WithSummary("Get WFS 2.0 capabilities document")
            .WithDescription("Alternative endpoint for GetCapabilities operation")
            .WithTags("WFS 2.0")
            .CacheOutput("Wfs20Capabilities")
            .Produces<WfsCapabilities>(200, "application/xml")
            .Produces<ExceptionReport>(400, "application/xml")
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles WFS 2.0 GetCapabilities request
    /// </summary>
    private static async Task<IResult> HandleGetCapabilities(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        string? acceptVersions,
        string? sections,
        string? acceptFormats,
        string? updateSequence,
        [FromServices] Wfs20Handler handler,
        [FromServices] ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        // Validate query parameters
        var validationError = Wfs20Utilities.ValidateRequestParameters(
            context.Request.Query,
            Wfs20Utilities.AllowedQueryParameters.GetCapabilities);

        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError);
        }

        // Validate specific parameters
        var requestParam = request ?? context.Request.Query[Wfs20Utilities.ParameterNames.Request].FirstOrDefault();
        if (!string.Equals(requestParam, Wfs20Utilities.Operations.GetCapabilities, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid request parameter. Expected '{Wfs20Utilities.Operations.GetCapabilities}', got '{requestParam}'");
        }

        // Parse accepted versions
        var acceptedVersions = ParseAcceptedVersions(acceptVersions);
        if (acceptedVersions.Count > 0 && !acceptedVersions.Contains(Wfs20Utilities.Version))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Unsupported version. This service supports only version {Wfs20Utilities.Version}");
        }

        try
        {
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var capabilities = await handler.HandleGetCapabilitiesAsync(
                context, acceptVersions, sections, baseUrl, context.RequestAborted);

            return SerializeXmlResponse(capabilities, "application/xml");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate WFS 2.0 GetCapabilities response");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to generate capabilities document");
        }
    }

    /// <summary>
    /// Alternative GetCapabilities endpoint that doesn't require query parameters
    /// </summary>
    private static async Task<IResult> HandleGetCapabilitiesAlt(
        HttpContext context,
        [FromServices] Wfs20Handler handler,
        [FromServices] ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        // Redirect to main endpoint with proper parameters
        var query = context.Request.Query;
        if (!query.ContainsKey("service"))
        {
            var queryBuilder = new StringBuilder();
            queryBuilder.Append($"service={Wfs20Utilities.ServiceType}");
            queryBuilder.Append($"&version={Wfs20Utilities.Version}");
            queryBuilder.Append($"&request={Wfs20Utilities.Operations.GetCapabilities}");

            // Preserve any existing parameters
            foreach (var kvp in query)
            {
                foreach (var value in kvp.Value)
                {
                    queryBuilder.Append($"&{kvp.Key}={Uri.EscapeDataString(value ?? "")}");
                }
            }

            return Results.Redirect($"/wfs?{queryBuilder}", permanent: true);
        }

        return await HandleGetCapabilities(context, null, null, null, null, null, null, null, handler, logger);
    }

    /// <summary>
    /// Builds the WFS capabilities document
    /// </summary>
    private static WfsCapabilities BuildCapabilities(HttpContext context, ImmutableHashSet<string> requestedSections)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var wfsUrl = $"{baseUrl}/wfs";

        var capabilities = new WfsCapabilities
        {
            ServiceIdentification = new ServiceIdentification(),
            ServiceProvider = new Models.ServiceProvider(),
            OperationsMetadata = BuildOperationsMetadata(wfsUrl),
            FeatureTypeList = BuildFeatureTypeList(baseUrl),
            FilterCapabilities = BuildFilterCapabilities()
        };

        return capabilities;
    }

    /// <summary>
    /// Builds operations metadata section
    /// </summary>
    private static OperationsMetadata BuildOperationsMetadata(string wfsUrl)
    {
        var operations = new[]
        {
            CreateOperation(Wfs20Utilities.Operations.GetCapabilities, wfsUrl, new[]
            {
                CreateParameter("AcceptVersions", Wfs20Utilities.Version),
                CreateParameter("Sections", "ServiceIdentification", "ServiceProvider", "OperationsMetadata", "FeatureTypeList", "Filter_Capabilities"),
                CreateParameter("AcceptFormats", "application/xml")
            }),

            CreateOperation(Wfs20Utilities.Operations.DescribeFeatureType, wfsUrl, new[]
            {
                CreateParameter("outputFormat", Wfs20Utilities.OutputFormats.All.ToArray()),
                CreateParameter("typeNames", allowAnyValue: true)
            }),

            CreateOperation(Wfs20Utilities.Operations.GetFeature, wfsUrl, new[]
            {
                CreateParameter("outputFormat", Wfs20Utilities.OutputFormats.All.ToArray()),
                CreateParameter("typeNames", allowAnyValue: true),
                CreateParameter("count", allowAnyValue: true),
                CreateParameter("startIndex", allowAnyValue: true),
                CreateParameter("sortBy", allowAnyValue: true),
                CreateParameter("filter", allowAnyValue: true),
                CreateParameter("bbox", allowAnyValue: true),
                CreateParameter("resourceId", allowAnyValue: true),
                CreateParameter("propertyName", allowAnyValue: true),
                CreateParameter("srsName", allowAnyValue: true)
            }),

            CreateOperation(Wfs20Utilities.Operations.GetPropertyValue, wfsUrl, new[]
            {
                CreateParameter("typeNames", allowAnyValue: true),
                CreateParameter("propertyName", allowAnyValue: true),
                CreateParameter("count", allowAnyValue: true),
                CreateParameter("startIndex", allowAnyValue: true),
                CreateParameter("filter", allowAnyValue: true),
                CreateParameter("bbox", allowAnyValue: true),
                CreateParameter("resourceId", allowAnyValue: true),
                CreateParameter("srsName", allowAnyValue: true)
            }),

            CreateOperation(Wfs20Utilities.Operations.Transaction, wfsUrl, Array.Empty<Parameter>(), postOnly: true)
        };

        return new OperationsMetadata
        {
            Operations = operations,
            Parameters = new[]
            {
                CreateParameter("version", Wfs20Utilities.Version),
                CreateParameter("service", Wfs20Utilities.ServiceType)
            },
            Constraints = new[]
            {
                new Constraint
                {
                    Name = "DefaultMaxFeatures",
                    DefaultValue = Wfs20Utilities.DefaultMaxFeatures.ToString()
                },
                new Constraint
                {
                    Name = "CountDefault",
                    DefaultValue = Wfs20Utilities.DefaultMaxFeatures.ToString()
                }
            }
        };
    }

    /// <summary>
    /// Creates an operation definition
    /// </summary>
    private static Operation CreateOperation(string name, string url, Parameter[]? parameters = null, bool postOnly = false)
    {
        var dcps = new List<DCP>();

        if (!postOnly)
        {
            dcps.Add(new DCP
            {
                Http = new Http
                {
                    Get = new[] { new Models.HttpMethod { Href = url } }
                }
            });
        }

        dcps.Add(new DCP
        {
            Http = new Http
            {
                Post = new[] { new Models.HttpMethod { Href = url } }
            }
        });

        return new Operation
        {
            Name = name,
            DCP = dcps.ToArray(),
            Parameters = parameters
        };
    }

    /// <summary>
    /// Creates a parameter definition
    /// </summary>
    private static Parameter CreateParameter(string name, params string[] allowedValues)
    {
        return new Parameter
        {
            Name = name,
            AllowedValues = allowedValues.Length > 0 ? new AllowedValues { Values = allowedValues } : null,
            AnyValue = allowedValues.Length == 0 ? new object() : null
        };
    }

    /// <summary>
    /// Creates a parameter that allows any value
    /// </summary>
    private static Parameter CreateParameter(string name, bool allowAnyValue)
    {
        return new Parameter
        {
            Name = name,
            AnyValue = allowAnyValue ? new object() : null
        };
    }

    /// <summary>
    /// Builds feature type list section
    /// </summary>
    private static FeatureTypeList BuildFeatureTypeList(string baseUrl)
    {
        // TODO: Integrate with layer catalog service to get actual feature types
        // For now, return empty list - this will be populated when integrating with the layer catalog
        return new FeatureTypeList
        {
            FeatureTypes = Array.Empty<FeatureType>()
        };
    }

    /// <summary>
    /// Builds filter capabilities section
    /// </summary>
    private static FilterCapabilities BuildFilterCapabilities()
    {
        return new FilterCapabilities
        {
            Conformance = new FesConformance
            {
                Constraints = new[]
                {
                    new FesConstraint { Name = "ImplementsQuery", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsAdHocQuery", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsResourceId", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsMinStandardFilter", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsStandardFilter", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsMinSpatialFilter", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsSpatialFilter", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsMinTemporalFilter", DefaultValue = "FALSE" },
                    new FesConstraint { Name = "ImplementsTemporalFilter", DefaultValue = "FALSE" },
                    new FesConstraint { Name = "ImplementsVersionNav", DefaultValue = "FALSE" },
                    new FesConstraint { Name = "ImplementsSorting", DefaultValue = "TRUE" },
                    new FesConstraint { Name = "ImplementsExtendedOperators", DefaultValue = "TRUE" }
                }
            },
            IdCapabilities = new IdCapabilities
            {
                ResourceIdentifiers = new[]
                {
                    new ResourceIdentifier { Name = "fid" },
                    new ResourceIdentifier { Name = "id" }
                }
            },
            ScalarCapabilities = new ScalarCapabilities
            {
                LogicalOperators = new object(),
                ComparisonOperators = new ComparisonOperators
                {
                    Operators = new[]
                    {
                        new ComparisonOperator { Name = "PropertyIsEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsNotEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsLessThan" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThan" },
                        new ComparisonOperator { Name = "PropertyIsLessThanOrEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsGreaterThanOrEqualTo" },
                        new ComparisonOperator { Name = "PropertyIsLike" },
                        new ComparisonOperator { Name = "PropertyIsNull" },
                        new ComparisonOperator { Name = "PropertyIsNil" },
                        new ComparisonOperator { Name = "PropertyIsBetween" }
                    }
                }
            },
            SpatialCapabilities = new SpatialCapabilities
            {
                GeometryOperands = new GeometryOperands
                {
                    Operands = new[]
                    {
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:Envelope" },
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:Point" },
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:LineString" },
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:Polygon" },
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:MultiPoint" },
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:MultiLineString" },
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:MultiPolygon" },
                        new GeometryOperand { Name = $"{Wfs20Utilities.GmlNamespace}:MultiGeometry" }
                    }
                },
                SpatialOperators = new SpatialOperators
                {
                    Operators = new[]
                    {
                        new SpatialOperator { Name = "BBOX" },
                        new SpatialOperator { Name = "Equals" },
                        new SpatialOperator { Name = "Disjoint" },
                        new SpatialOperator { Name = "Intersects" },
                        new SpatialOperator { Name = "Touches" },
                        new SpatialOperator { Name = "Crosses" },
                        new SpatialOperator { Name = "Within" },
                        new SpatialOperator { Name = "Contains" },
                        new SpatialOperator { Name = "Overlaps" },
                        new SpatialOperator { Name = "Beyond" },
                        new SpatialOperator { Name = "DWithin" }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Parses the AcceptVersions parameter
    /// </summary>
    private static ImmutableHashSet<string> ParseAcceptedVersions(string? acceptVersions)
    {
        if (string.IsNullOrEmpty(acceptVersions))
            return ImmutableHashSet<string>.Empty;

        return acceptVersions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the Sections parameter
    /// </summary>
    private static ImmutableHashSet<string> ParseSections(string? sections)
    {
        if (string.IsNullOrEmpty(sections))
            return ImmutableHashSet<string>.Empty;

        return sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a WFS exception response
    /// </summary>
    private static IResult CreateExceptionResponse(HttpContext context, string exceptionCode, string exceptionText, string? locator)
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

        context.Response.StatusCode = 400;
        return SerializeXmlResponse(exceptionReport, "application/xml");
    }

    /// <summary>
    /// Serializes an object to XML and returns as IResult
    /// </summary>
    private static IResult SerializeXmlResponse<T>(T obj, string contentType) where T : class
    {
        var serializer = new XmlSerializer(typeof(T));
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, settings);

        serializer.Serialize(xmlWriter, obj);

        var xmlContent = stringWriter.ToString();
        return Results.Content(xmlContent, contentType, Encoding.UTF8);
    }

}
