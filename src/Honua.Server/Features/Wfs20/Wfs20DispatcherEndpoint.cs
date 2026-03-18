// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Wfs20.Models;
using Honua.Server.Features.Wfs20.Services;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// WFS 2.0 dispatcher endpoint that routes requests based on the 'request' parameter
/// </summary>
internal static class Wfs20DispatcherEndpoint
{
    private delegate Task<IResult> WfsOperationHandler(
        HttpContext context, string? service, string? version, string? request,
        Wfs20Handler handler, ILogger logger);

    /// <summary>
    /// Maps implemented WFS operation names to their handler methods.
    /// This is the single source of truth: <see cref="ImplementedOperations"/> is derived from
    /// its keys, and the dispatcher looks up handlers here instead of using a separate switch.
    /// Excludes stubs (e.g. Transaction) that return "not implemented".
    /// </summary>
    private static readonly Dictionary<string, WfsOperationHandler> _operationHandlers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["GetCapabilities"] = HandleGetCapabilities,
            ["DescribeFeatureType"] = HandleDescribeFeatureType,
            ["GetFeature"] = HandleGetFeature,
            ["GetPropertyValue"] = HandleGetPropertyValue,
        };

    /// <summary>
    /// WFS operations that have full handler implementations.
    /// Derived from <see cref="_operationHandlers"/> to guarantee consistency with dispatch logic.
    /// Used by architecture drift tests to verify <see cref="OperationRegistry"/> stays in sync.
    /// </summary>
    internal static readonly IReadOnlySet<string> ImplementedOperations =
        new HashSet<string>(_operationHandlers.Keys, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] HttpMethods = new[] { "GET", "POST" };
    /// <summary>
    /// Maps the single WFS 2.0 dispatcher endpoint
    /// </summary>
    internal static IEndpointRouteBuilder MapWfs20DispatcherEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // Single WFS endpoint that dispatches based on request parameter
        endpoints.MapMethods("/wfs", HttpMethods, HandleWfsRequest)
            .WithDisplayName("WFS 2.0 Service")
            .WithName("Wfs20Service")
            .WithSummary("OGC Web Feature Service 2.0")
            .WithDescription("Handles all WFS 2.0 operations: GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction")
            .WithTags("WFS 2.0", "OGC")
            .Produces<object>(200, "application/xml")
            .Produces<ExceptionReport>(400, "application/xml")
            .Produces(404)
            .Produces(405);

        return endpoints;
    }

    /// <summary>
    /// Handles all WFS 2.0 requests and dispatches to appropriate operation handler
    /// </summary>
    private static async Task<IResult> HandleWfsRequest(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        Wfs20Handler handler,
        ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        try
        {
            // Get request parameter (case insensitive)
            var requestParam = request ?? context.Request.Query[Wfs20Utilities.ParameterNames.Request].FirstOrDefault();

            if (string.IsNullOrEmpty(requestParam))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Missing required 'request' parameter. Supported operations: GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction");
            }

            // Dispatch via the handler dictionary (single source of truth for implemented operations).
            if (_operationHandlers.TryGetValue(requestParam, out var operationHandler))
            {
                return await operationHandler(context, service, version, request, handler, logger);
            }

            // Transaction is a known but unimplemented stub — kept out of _operationHandlers
            // so it does not appear in ImplementedOperations or trigger coverage requirements.
            if (string.Equals(requestParam, "TRANSACTION", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleTransaction(context, service, version, request, handler, logger);
            }

            return StandardErrorHelpers.CreateBadRequest(context,
                $"Unsupported operation '{requestParam}'. Supported operations: GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process WFS 2.0 request for operation: {Operation}", request);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process WFS request");
        }
    }

    /// <summary>
    /// Handles GetCapabilities operation
    /// </summary>
    private static async Task<IResult> HandleGetCapabilities(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        Wfs20Handler handler,
        ILogger logger)
    {
        // Validate query parameters
        var validationError = Wfs20Utilities.ValidateRequestParameters(
            context.Request.Query,
            Wfs20Utilities.AllowedQueryParameters.GetCapabilities);

        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError);
        }

        // Validate service parameter
        var serviceParam = service ?? context.Request.Query[Wfs20Utilities.ParameterNames.Service].FirstOrDefault();
        if (!string.Equals(serviceParam, Wfs20Utilities.ServiceType, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid service parameter. Expected '{Wfs20Utilities.ServiceType}', got '{serviceParam}'");
        }

        // Parse accepted versions
        var acceptVersions = context.Request.Query[Wfs20Utilities.ParameterNames.AcceptVersions].FirstOrDefault();
        if (!string.IsNullOrEmpty(acceptVersions))
        {
            var acceptedVersions = acceptVersions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (acceptedVersions.Length > 0 && !acceptedVersions.Contains(Wfs20Utilities.Version, StringComparer.OrdinalIgnoreCase))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Unsupported version. This service supports only version {Wfs20Utilities.Version}");
            }
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var sections = context.Request.Query[Wfs20Utilities.ParameterNames.Sections].FirstOrDefault();

        var capabilities = await handler.HandleGetCapabilitiesAsync(
            context, acceptVersions, sections, baseUrl, context.RequestAborted);

        return SerializeXmlResponse(capabilities, "application/xml");
    }

    /// <summary>
    /// Handles DescribeFeatureType operation
    /// </summary>
    private static async Task<IResult> HandleDescribeFeatureType(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            // Validate query parameters
            var validationError = Wfs20Utilities.ValidateRequestParameters(
                context.Request.Query,
                Wfs20Utilities.AllowedQueryParameters.DescribeFeatureType);

            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError);
            }

            // Extract parameters
            var typeNames = context.Request.Query[Wfs20Utilities.ParameterNames.TypeNames].FirstOrDefault();
            var outputFormat = context.Request.Query[Wfs20Utilities.ParameterNames.OutputFormat].FirstOrDefault();

            // Validate output format
            var normalizedFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
            if (!string.Equals(normalizedFormat, "application/xml", StringComparison.OrdinalIgnoreCase) &&
                !normalizedFormat.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Unsupported output format '{outputFormat}'. DescribeFeatureType requires XML-based formats.");
            }

            // Handle the request
            var schema = await handler.HandleDescribeFeatureTypeAsync(
                context, typeNames, outputFormat, context.RequestAborted);

            return Results.Content(schema, "application/xml", System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process DescribeFeatureType request");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process DescribeFeatureType request");
        }
    }

    /// <summary>
    /// Handles GetFeature operation
    /// </summary>
    private static async Task<IResult> HandleGetFeature(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            // Validate query parameters
            var validationError = Wfs20Utilities.ValidateRequestParameters(
                context.Request.Query,
                Wfs20Utilities.AllowedQueryParameters.GetFeature);

            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError);
            }

            // Extract parameters
            var typeNames = context.Request.Query[Wfs20Utilities.ParameterNames.TypeNames].FirstOrDefault();
            var outputFormat = context.Request.Query[Wfs20Utilities.ParameterNames.OutputFormat].FirstOrDefault();
            var count = context.Request.Query[Wfs20Utilities.ParameterNames.Count].FirstOrDefault();
            var startIndex = context.Request.Query[Wfs20Utilities.ParameterNames.StartIndex].FirstOrDefault();
            var sortBy = context.Request.Query[Wfs20Utilities.ParameterNames.SortBy].FirstOrDefault();
            var bbox = context.Request.Query[Wfs20Utilities.ParameterNames.BBox].FirstOrDefault();
            var filter = context.Request.Query[Wfs20Utilities.ParameterNames.Filter].FirstOrDefault();
            var resourceId = context.Request.Query[Wfs20Utilities.ParameterNames.ResourceId].FirstOrDefault();
            var propertyName = context.Request.Query[Wfs20Utilities.ParameterNames.PropertyName].FirstOrDefault();
            var srsName = context.Request.Query[Wfs20Utilities.ParameterNames.SrsName].FirstOrDefault();

            // Handle the request
            return await handler.HandleGetFeatureAsync(
                context, typeNames, outputFormat, count, startIndex, sortBy, bbox, filter, resourceId, propertyName, srsName,
                context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process GetFeature request");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process GetFeature request");
        }
    }

    /// <summary>
    /// Handles GetPropertyValue operation
    /// </summary>
    private static async Task<IResult> HandleGetPropertyValue(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            // Validate query parameters
            var validationError = Wfs20Utilities.ValidateRequestParameters(
                context.Request.Query,
                Wfs20Utilities.AllowedQueryParameters.GetPropertyValue);

            if (validationError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, validationError);
            }

            // Extract parameters
            var typeNames = context.Request.Query[Wfs20Utilities.ParameterNames.TypeNames].FirstOrDefault();
            var outputFormat = context.Request.Query[Wfs20Utilities.ParameterNames.OutputFormat].FirstOrDefault();
            var valueReference = context.Request.Query[Wfs20Utilities.ParameterNames.ValueReference].FirstOrDefault()
                ?? context.Request.Query[Wfs20Utilities.ParameterNames.PropertyName].FirstOrDefault();
            var count = context.Request.Query[Wfs20Utilities.ParameterNames.Count].FirstOrDefault();
            var startIndex = context.Request.Query[Wfs20Utilities.ParameterNames.StartIndex].FirstOrDefault();
            var bbox = context.Request.Query[Wfs20Utilities.ParameterNames.BBox].FirstOrDefault();
            var filter = context.Request.Query[Wfs20Utilities.ParameterNames.Filter].FirstOrDefault();
            var resourceId = context.Request.Query[Wfs20Utilities.ParameterNames.ResourceId].FirstOrDefault();
            var srsName = context.Request.Query[Wfs20Utilities.ParameterNames.SrsName].FirstOrDefault();

            // Handle the request
            return await handler.HandleGetPropertyValueAsync(
                context, typeNames, valueReference, outputFormat, count, startIndex, bbox, filter, resourceId, srsName,
                context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process GetPropertyValue request");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process GetPropertyValue request");
        }
    }

    /// <summary>
    /// Handles Transaction operation
    /// </summary>
    private static Task<IResult> HandleTransaction(
        HttpContext context,
        string? service,
        string? version,
        string? request,
        Wfs20Handler handler,
        ILogger logger)
    {
        // TODO: Implement Transaction operation
        logger.LogWarning("Transaction operation not yet implemented");
        return Task.FromResult(StandardErrorHelpers.CreateNotImplemented(context, "Transaction operation not yet implemented"));
    }

    /// <summary>
    /// Serializes an object to XML and returns as IResult
    /// </summary>
    private static IResult SerializeXmlResponse<T>(T obj, string contentType) where T : class
    {
        var xmlContent = XmlResultSerializer.Serialize(obj);
        return Results.Content(xmlContent, contentType, System.Text.Encoding.UTF8);
    }
}
