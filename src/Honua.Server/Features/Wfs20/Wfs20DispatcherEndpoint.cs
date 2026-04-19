// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Wfs20.Models;
using Honua.Server.Features.Wfs20.Services;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// WFS 2.0 dispatcher endpoint that routes requests based on the 'request' parameter
/// </summary>
internal static class Wfs20DispatcherEndpoint
{
    private delegate Task<IResult> WfsOperationHandler(
        HttpContext context, WfsRequestParameters parameters,
        Wfs20Handler handler, ILogger logger);

    private sealed record WfsValidationError(string ExceptionCode, string? Locator, string Detail);

    private sealed class WfsRequestParameters
    {
        private readonly Dictionary<string, string> _values;

        public WfsRequestParameters(Dictionary<string, string> values, int xmlQueryCount = 0)
        {
            _values = values;
            XmlQueryCount = xmlQueryCount;
        }

        public int XmlQueryCount { get; }

        public bool HasMultipleXmlQueries => XmlQueryCount > 1;

        public bool Contains(string primaryName, params string[] aliases)
        {
            if (_values.ContainsKey(primaryName))
            {
                return true;
            }

            return aliases.Any(alias => _values.ContainsKey(alias));
        }

        public string? Get(string primaryName, params string[] aliases)
        {
            if (_values.TryGetValue(primaryName, out var primaryValue) &&
                !string.IsNullOrWhiteSpace(primaryValue))
            {
                return primaryValue;
            }

            foreach (var alias in aliases)
            {
                if (_values.TryGetValue(alias, out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
    }

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
            ["Transaction"] = HandleTransaction,
            ["ListStoredQueries"] = HandleListStoredQueries,
            ["DescribeStoredQueries"] = HandleDescribeStoredQueries,
        };

    /// <summary>
    /// WFS operations that have full handler implementations.
    /// Derived from <see cref="_operationHandlers"/> to guarantee consistency with dispatch logic.
    /// Used by architecture drift tests to verify <see cref="OperationRegistry"/> stays in sync.
    /// </summary>
    internal static readonly IReadOnlySet<string> ImplementedOperations =
        new HashSet<string>(_operationHandlers.Keys, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SupportedHttpMethods = new[] { "GET", "POST" };
    /// <summary>
    /// Maps the single WFS 2.0 dispatcher endpoint
    /// </summary>
    internal static IEndpointRouteBuilder MapWfs20DispatcherEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // Single WFS endpoint that dispatches based on request parameter
        endpoints.MapMethods("/wfs", SupportedHttpMethods, HandleWfsRequest)
            .WithDisplayName("WFS 2.0 Service")
            .WithName("Wfs20Service")
            .WithSummary("OGC Web Feature Service 2.0")
            .WithDescription("Handles all WFS 2.0 operations: GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction, ListStoredQueries, DescribeStoredQueries")
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
        Wfs20Handler handler,
        ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        try
        {
            var parameters = await ReadRequestParametersAsync(context);
            var requestParam = parameters.Get(Wfs20Utilities.ParameterNames.Request);

            if (string.IsNullOrEmpty(requestParam))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "MissingParameterValue",
                    "Missing required 'request' parameter. Supported operations: GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction, ListStoredQueries, DescribeStoredQueries",
                    "request");
            }

            // Dispatch via the handler dictionary (single source of truth for implemented operations).
            if (_operationHandlers.TryGetValue(requestParam, out var operationHandler))
            {
                return await operationHandler(context, parameters, handler, logger);
            }

            return Wfs20ErrorResults.CreateNotImplemented(
                context,
                "OperationNotSupported",
                $"Unsupported operation '{requestParam}'. Supported operations: GetCapabilities, DescribeFeatureType, GetFeature, GetPropertyValue, Transaction, ListStoredQueries, DescribeStoredQueries",
                "request");
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Invalid WFS 2.0 XML request body");
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                "Invalid WFS XML request body.",
                "request");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process WFS 2.0 request");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process WFS request");
        }
    }

    /// <summary>
    /// Handles GetCapabilities operation
    /// </summary>
    private static async Task<IResult> HandleGetCapabilities(
        HttpContext context,
        WfsRequestParameters parameters,
        Wfs20Handler handler,
        ILogger logger)
    {
        var validationError = ValidateCapabilitiesRequestParameters(parameters);

        if (validationError is not null)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                validationError.ExceptionCode,
                validationError.Detail,
                validationError.Locator);
        }

        // Parse accepted versions
        var acceptVersions = parameters.Get(Wfs20Utilities.ParameterNames.AcceptVersions);
        if (!string.IsNullOrEmpty(acceptVersions))
        {
            var acceptedVersions = acceptVersions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (acceptedVersions.Length > 0 && !acceptedVersions.Contains(Wfs20Utilities.Version, StringComparer.OrdinalIgnoreCase))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "VersionNegotiationFailed",
                    $"Unsupported version. This service supports only version {Wfs20Utilities.Version}");
            }
        }

        // GetCapabilities is inherently XML-only; reject on the Accept header would
        // break clients that set a broad default like `application/json` as a
        // catch-all (common in test fixtures and OpenAPI-typed SDK clients).
        // The server will still respond with application/xml regardless.

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var sections = parameters.Get(Wfs20Utilities.ParameterNames.Sections);
        if (!Wfs20Utilities.TryParseSections(sections, out var requestedSections, out var sectionsError))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                sectionsError!,
                "sections");
        }

        var updateSequence = parameters.Get(Wfs20Utilities.ParameterNames.UpdateSequence);
        if (Wfs20Utilities.CompareUpdateSequence(updateSequence, Wfs20Utilities.CurrentUpdateSequence) > 0)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidUpdateSequence",
                $"UPDATESEQUENCE '{updateSequence}' is newer than the current capabilities document.",
                "updateSequence");
        }

        var capabilities = await handler.HandleGetCapabilitiesAsync(
            context, acceptVersions, requestedSections, baseUrl, context.RequestAborted);

        return SerializeXmlResponse(capabilities, "application/xml");
    }

    /// <summary>
    /// Handles DescribeFeatureType operation
    /// </summary>
    private static async Task<IResult> HandleDescribeFeatureType(
        HttpContext context,
        WfsRequestParameters parameters,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            var validationError = ValidateOperationRequestParameters(parameters);

            if (validationError is not null)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    validationError.ExceptionCode,
                    validationError.Detail,
                    validationError.Locator);
            }

            var typeNames = parameters.Get(
                Wfs20Utilities.ParameterNames.TypeNames,
                Wfs20Utilities.ParameterNames.TypeName);
            var outputFormat = parameters.Get(Wfs20Utilities.ParameterNames.OutputFormat);

            // Validate output format
            var normalizedFormat = Wfs20Utilities.OutputFormats.NormalizeOutputFormat(outputFormat);
            if (!string.Equals(normalizedFormat, "application/xml", StringComparison.OrdinalIgnoreCase) &&
                !normalizedFormat.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Unsupported output format '{outputFormat}'. DescribeFeatureType requires XML-based formats.",
                    "outputFormat");
            }

            // DescribeFeatureType's outputFormat check above already enforces an
            // XML-compatible format. Don't also reject on the Accept header —
            // test fixtures and catch-all SDK clients routinely set
            // `Accept: application/json` and the server always emits XML here
            // regardless.

            // Handle the request
            var schema = await handler.HandleDescribeFeatureTypeAsync(
                context, typeNames, outputFormat, context.RequestAborted);

            return Results.Content(schema, "application/xml", System.Text.Encoding.UTF8);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "DescribeFeatureType validation failed");
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                ex.Message,
                "typeNames");
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
        WfsRequestParameters parameters,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            if (parameters.HasMultipleXmlQueries)
            {
                return Wfs20ErrorResults.CreateNotImplemented(
                    context,
                    "OperationNotSupported",
                    "POST XML GetFeature requests with multiple wfs:Query elements are not supported.",
                    "Query");
            }

            var validationError = ValidateOperationRequestParameters(parameters);

            if (validationError is not null)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    validationError.ExceptionCode,
                    validationError.Detail,
                    validationError.Locator);
            }

            var typeNames = parameters.Get(
                Wfs20Utilities.ParameterNames.TypeNames,
                Wfs20Utilities.ParameterNames.TypeName);
            var outputFormat = parameters.Get(Wfs20Utilities.ParameterNames.OutputFormat);
            var count = parameters.Get(
                Wfs20Utilities.ParameterNames.Count,
                Wfs20Utilities.ParameterNames.MaxFeatures);
            var startIndex = parameters.Get(Wfs20Utilities.ParameterNames.StartIndex);
            var sortBy = parameters.Get(Wfs20Utilities.ParameterNames.SortBy);
            var bbox = parameters.Get(Wfs20Utilities.ParameterNames.BBox);
            var filter = parameters.Get(Wfs20Utilities.ParameterNames.Filter);
            var resourceId = parameters.Get(
                Wfs20Utilities.ParameterNames.ResourceId,
                Wfs20Utilities.ParameterNames.FeatureId);
            var storedQueryId = parameters.Get(Wfs20Utilities.ParameterNames.StoredQueryId);
            var storedQueryFeatureId = parameters.Get(Wfs20Utilities.ParameterNames.Id, "id");
            var propertyName = parameters.Get(Wfs20Utilities.ParameterNames.PropertyName);
            var srsName = parameters.Get(
                Wfs20Utilities.ParameterNames.SrsName,
                Wfs20Utilities.ParameterNames.Srs);
            var resultType = parameters.Get(Wfs20Utilities.ParameterNames.ResultType);

            if (!string.IsNullOrWhiteSpace(storedQueryId))
            {
                return await handler.HandleStoredQueryGetFeatureAsync(
                    context,
                    storedQueryId,
                    storedQueryFeatureId,
                    outputFormat,
                    count,
                    context.RequestAborted);
            }

            // Handle the request
            return await handler.HandleGetFeatureAsync(
                context, typeNames, outputFormat, count, startIndex, sortBy, bbox, filter, resourceId, propertyName, srsName, resultType,
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
        WfsRequestParameters parameters,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            if (parameters.HasMultipleXmlQueries)
            {
                return Wfs20ErrorResults.CreateNotImplemented(
                    context,
                    "OperationNotSupported",
                    "POST XML GetPropertyValue requests with multiple wfs:Query elements are not supported.",
                    "Query");
            }

            var validationError = ValidateOperationRequestParameters(parameters);

            if (validationError is not null)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    validationError.ExceptionCode,
                    validationError.Detail,
                    validationError.Locator);
            }

            var typeNames = parameters.Get(
                Wfs20Utilities.ParameterNames.TypeNames,
                Wfs20Utilities.ParameterNames.TypeName);
            var outputFormat = parameters.Get(Wfs20Utilities.ParameterNames.OutputFormat);
            var valueReference = parameters.Get(
                Wfs20Utilities.ParameterNames.ValueReference,
                Wfs20Utilities.ParameterNames.PropertyName);
            var valueReferenceSpecified = parameters.Contains(
                Wfs20Utilities.ParameterNames.ValueReference,
                Wfs20Utilities.ParameterNames.PropertyName);
            var count = parameters.Get(
                Wfs20Utilities.ParameterNames.Count,
                Wfs20Utilities.ParameterNames.MaxFeatures);
            var startIndex = parameters.Get(Wfs20Utilities.ParameterNames.StartIndex);
            var bbox = parameters.Get(Wfs20Utilities.ParameterNames.BBox);
            var filter = parameters.Get(Wfs20Utilities.ParameterNames.Filter);
            var resourceId = parameters.Get(
                Wfs20Utilities.ParameterNames.ResourceId,
                Wfs20Utilities.ParameterNames.FeatureId);
            var srsName = parameters.Get(
                Wfs20Utilities.ParameterNames.SrsName,
                Wfs20Utilities.ParameterNames.Srs);

            // Handle the request
            return await handler.HandleGetPropertyValueAsync(
                context, typeNames, valueReference, valueReferenceSpecified, outputFormat, count, startIndex, bbox, filter, resourceId, srsName,
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
    private static async Task<IResult> HandleTransaction(
        HttpContext context,
        WfsRequestParameters parameters,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            var validationError = ValidateOperationRequestParameters(parameters);

            if (validationError is not null)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    validationError.ExceptionCode,
                    validationError.Detail,
                    validationError.Locator);
            }

            return await handler.HandleTransactionAsync(context, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process Transaction request");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process Transaction request");
        }
    }

    private static async Task<IResult> HandleListStoredQueries(
        HttpContext context,
        WfsRequestParameters parameters,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            var validationError = ValidateOperationRequestParameters(parameters);

            if (validationError is not null)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    validationError.ExceptionCode,
                    validationError.Detail,
                    validationError.Locator);
            }

            return await handler.HandleListStoredQueriesAsync(context, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process ListStoredQueries request");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process ListStoredQueries request");
        }
    }

    private static async Task<IResult> HandleDescribeStoredQueries(
        HttpContext context,
        WfsRequestParameters parameters,
        Wfs20Handler handler,
        ILogger logger)
    {
        try
        {
            var validationError = ValidateOperationRequestParameters(parameters);

            if (validationError is not null)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    validationError.ExceptionCode,
                    validationError.Detail,
                    validationError.Locator);
            }

            var storedQueryIds = parameters.Get(Wfs20Utilities.ParameterNames.StoredQueryId);
            return await handler.HandleDescribeStoredQueriesAsync(context, storedQueryIds, context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process DescribeStoredQueries request");
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process DescribeStoredQueries request");
        }
    }

    private static WfsValidationError? ValidateCapabilitiesRequestParameters(WfsRequestParameters parameters)
    {
        var service = parameters.Get(Wfs20Utilities.ParameterNames.Service);
        if (!string.IsNullOrWhiteSpace(service) &&
            !string.Equals(service, Wfs20Utilities.ServiceType, StringComparison.OrdinalIgnoreCase))
        {
            return new WfsValidationError(
                "InvalidParameterValue",
                "service",
                $"Invalid service parameter. Expected '{Wfs20Utilities.ServiceType}', got '{service}'.");
        }

        var version = parameters.Get(Wfs20Utilities.ParameterNames.Version);
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        if (!string.Equals(version, Wfs20Utilities.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new WfsValidationError(
                "VersionNegotiationFailed",
                null,
                $"Unsupported version. Expected '{Wfs20Utilities.Version}', got '{version}'.");
        }

        return null;
    }

    private static WfsValidationError? ValidateOperationRequestParameters(WfsRequestParameters parameters)
    {
        var service = parameters.Get(Wfs20Utilities.ParameterNames.Service);
        if (string.IsNullOrWhiteSpace(service))
        {
            return new WfsValidationError(
                "MissingParameterValue",
                "service",
                "Missing required 'service' parameter.");
        }

        if (!string.Equals(service, Wfs20Utilities.ServiceType, StringComparison.OrdinalIgnoreCase))
        {
            return new WfsValidationError(
                "InvalidParameterValue",
                "service",
                $"Invalid service parameter. Expected '{Wfs20Utilities.ServiceType}', got '{service}'.");
        }

        var version = parameters.Get(Wfs20Utilities.ParameterNames.Version);
        if (string.IsNullOrWhiteSpace(version))
        {
            return new WfsValidationError(
                "MissingParameterValue",
                "version",
                "Missing required 'version' parameter.");
        }

        if (!string.Equals(version, Wfs20Utilities.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new WfsValidationError(
                "VersionNegotiationFailed",
                null,
                $"Unsupported version. Expected '{Wfs20Utilities.Version}', got '{version}'.");
        }

        return null;
    }

    private static async Task<WfsRequestParameters> ReadRequestParametersAsync(HttpContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in context.Request.Query.Keys)
        {
            var value = context.Request.Query[key].FirstOrDefault();
            values[key] = value?.Trim() ?? string.Empty;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return new WfsRequestParameters(values);
        }

        context.Request.EnableBuffering();
        using var reader = new StreamReader(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(body))
        {
            return new WfsRequestParameters(values);
        }

        XDocument document;
        try
        {
            document = SecureXmlDocumentParser.Parse(body, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Invalid WFS XML request body.", ex);
        }

        var root = document.Root ?? throw new InvalidDataException("Invalid WFS XML request body.");
        var xmlQueryCount = ApplyXmlParameters(root, values);
        return new WfsRequestParameters(values, xmlQueryCount);
    }

    private static int ApplyXmlParameters(XElement root, Dictionary<string, string> values)
    {
        SetValue(values, Wfs20Utilities.ParameterNames.Request, root.Name.LocalName);
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.Service, "service");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.Version, "version");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.OutputFormat, "outputFormat");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.Count, "count", "maxFeatures");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.StartIndex, "startIndex");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.ResultType, "resultType");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.ValueReference, "valueReference");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.Sections, "sections");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.AcceptFormats, "acceptFormats");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.AcceptVersions, "acceptVersions");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.UpdateSequence, "updateSequence");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.TypeNames, "typeNames");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.TypeName, "typeName");
        CopyAttribute(root, values, Wfs20Utilities.ParameterNames.StoredQueryId, "storedQueryId");

        var typeNames = root
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "TypeName", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (typeNames.Length > 0)
        {
            SetValue(values, Wfs20Utilities.ParameterNames.TypeNames, string.Join(",", typeNames));
        }

        var queries = root
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Query", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (queries.Length > 0)
        {
            var queryTypeNames = queries
                .SelectMany(query => query.Attributes()
                    .Where(attribute => string.Equals(attribute.Name.LocalName, "typeNames", StringComparison.OrdinalIgnoreCase))
                    .Select(attribute => attribute.Value.Trim()))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (queryTypeNames.Length > 0)
            {
                SetValue(values, Wfs20Utilities.ParameterNames.TypeNames, string.Join(",", queryTypeNames));
            }

            var propertyNames = queries
                .SelectMany(query => query.Elements()
                    .Where(element => string.Equals(element.Name.LocalName, "PropertyName", StringComparison.OrdinalIgnoreCase))
                    .Select(element => element.Value.Trim()))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (propertyNames.Length > 0)
            {
                SetValue(values, Wfs20Utilities.ParameterNames.PropertyName, string.Join(",", propertyNames));
            }

            var srsName = queries
                .SelectMany(query => query.Attributes()
                    .Where(attribute => string.Equals(attribute.Name.LocalName, "srsName", StringComparison.OrdinalIgnoreCase))
                    .Select(attribute => attribute.Value.Trim()))
                .FirstOrDefault(value => value.Length > 0);
            if (!string.IsNullOrWhiteSpace(srsName))
            {
                SetValue(values, Wfs20Utilities.ParameterNames.SrsName, srsName);
            }

            var filter = queries
                .SelectMany(query => query.Elements()
                    .Where(element => string.Equals(element.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase))
                    .Select(element => element.ToString(SaveOptions.DisableFormatting)))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(filter))
            {
                SetValue(values, Wfs20Utilities.ParameterNames.Filter, filter);
            }
        }

        var storedQuery = root.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "StoredQuery", StringComparison.OrdinalIgnoreCase));
        if (storedQuery != null)
        {
            CopyAttribute(storedQuery, values, Wfs20Utilities.ParameterNames.StoredQueryId, "id");

            var storedQueryFeatureId = storedQuery.Elements()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "Parameter", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        element.Attributes()
                            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase))
                            ?.Value,
                        "id",
                        StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (!string.IsNullOrWhiteSpace(storedQueryFeatureId))
            {
                SetValue(values, Wfs20Utilities.ParameterNames.Id, storedQueryFeatureId);
            }
        }

        var storedQueryIds = root
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "StoredQueryId", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (storedQueryIds.Length > 0)
        {
            SetValue(values, Wfs20Utilities.ParameterNames.StoredQueryId, string.Join(",", storedQueryIds));
        }

        CopyElementValue(root, values, Wfs20Utilities.ParameterNames.ValueReference, "ValueReference");
        return queries.Length;
    }

    private static void CopyAttribute(
        XElement element,
        Dictionary<string, string> values,
        string targetName,
        params string[] attributeNames)
    {
        foreach (var attributeName in attributeNames)
        {
            var value = element.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (value is not null)
            {
                SetRawValue(values, targetName, value);
                return;
            }
        }
    }

    private static void CopyElementValue(
        XElement root,
        Dictionary<string, string> values,
        string targetName,
        string elementName)
    {
        var value = root.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (value is not null)
        {
            SetRawValue(values, targetName, value);
        }
    }

    private static void SetValue(Dictionary<string, string> values, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }

    private static void SetRawValue(Dictionary<string, string> values, string key, string value)
        => values[key] = value.Trim();

    /// <summary>
    /// Serializes an object to XML and returns as IResult
    /// </summary>
    private static IResult SerializeXmlResponse<T>(T obj, string contentType) where T : class
    {
        var xmlContent = XmlResultSerializer.Serialize(obj);
        return Results.Content(xmlContent, contentType, System.Text.Encoding.UTF8);
    }
}
