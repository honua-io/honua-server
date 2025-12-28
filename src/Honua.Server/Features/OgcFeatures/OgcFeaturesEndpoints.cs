// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using InfraLog = Honua.Server.Features.Infrastructure.Logging.Log;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Extension methods to register OGC API Features endpoints
/// </summary>
internal static partial class OgcFeaturesEndpoints
{
    internal sealed class OgcFeaturesEndpointsLog
    {
    }

    /// <summary>
    /// Maps OGC API Features endpoints for Core and Conformance
    /// </summary>
    public static IEndpointRouteBuilder MapOgcFeaturesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features", HandleGetLandingPage)
            .WithDisplayName("OGC API Features Landing Page")
            .WithName("OgcLandingPage")
            .WithSummary("Get OGC API Features landing page")
            .WithDescription("The landing page provides links to the API definition and other resources")
            .WithTags("OGC API Features")
            .CacheOutput("OgcLandingPage")
            .Produces<LandingPage>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Features Conformance")
            .WithName("OgcConformance")
            .WithSummary("Get OGC API Features conformance declaration")
            .WithDescription("Conformance classes that this API conforms to")
            .WithTags("OGC API Features")
            .CacheOutput("OgcConformance")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/openapi.json", HandleGetOpenApiSpec)
            .WithDisplayName("OGC API Features OpenAPI Specification")
            .WithName("OpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API Features")
            .WithDescription("The OpenAPI specification describes all available endpoints and their parameters")
            .WithTags("OGC API Features")
            .CacheOutput("OgcOpenApi")
            .Produces<object>(200, MediaTypes.OpenApi)
            .Produces(404);

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
            .WithName("GetQueryables")
            .WithSummary("Get queryable properties for a collection")
            .WithDescription("Get the JSON Schema describing the queryable properties for filtering features in this collection")
            .WithTags("OGC API Features")
            .CacheOutput("OgcQueryables")
            .Produces<QueryablesSchema>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items", HandleGetItems)
            .WithDisplayName("OGC API Features Items")
            .WithName("GetItems")
            .WithSummary("Get features from a collection")
            .WithDescription("Get features from a collection with optional filtering using CQL2-Text")
            .WithTags("OGC API Features")
            .Produces<FeatureCollection>(200, MediaTypes.GeoJson)
            .Produces<FeatureCollection>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Gml)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items/{featureId}", HandleGetItem)
            .WithDisplayName("OGC API Features Item")
            .WithName("GetItem")
            .WithSummary("Get a specific feature from a collection")
            .WithDescription("Get a specific feature by its ID from a collection")
            .WithTags("OGC API Features")
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Gml)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404)
            .Produces(400);

        // OGC API Features Transaction Support (Issue #18)
        endpoints.MapPost("/ogc/features/collections/{collectionId}/items", HandleCreateFeature)
            .WithDisplayName("OGC API Features Create")
            .WithName("CreateFeature")
            .WithSummary("Create a new feature in a collection")
            .WithDescription("Add a new feature to the specified collection")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(201, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .Produces(409); // Conflict if feature ID already exists

        endpoints.MapPut("/ogc/features/collections/{collectionId}/items/{featureId}", HandleUpdateFeature)
            .WithDisplayName("OGC API Features Update")
            .WithName("UpdateFeature")
            .WithSummary("Update an existing feature")
            .WithDescription("Replace an existing feature with new data")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces(201) // If feature didn't exist (upsert behavior)
            .Produces(400)
            .Produces(404);

        endpoints.MapDelete("/ogc/features/collections/{collectionId}/items/{featureId}", HandleDeleteFeature)
            .WithDisplayName("OGC API Features Delete")
            .WithName("DeleteFeature")
            .WithSummary("Delete a feature from a collection")
            .WithDescription("Remove a feature from the specified collection")
            .WithTags("OGC API Features", "Transactions")
            .Produces(204) // No Content - successful deletion
            .Produces(404)
            .Produces(400);

        return endpoints;
    }

    private static class AllowedQueryParameters
    {
        public static readonly ISet<string> Metadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f"
        };

        public static readonly ISet<string> Items = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f",
            "bbox",
            "bbox-crs",
            "crs",
            "datetime",
            "limit",
            "offset",
            "filter",
            "filter-lang",
            "filter-crs"
        };

        public static readonly ISet<string> Item = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f",
            "crs"
        };

        public static readonly ISet<string> OpenApi = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f"
        };

        public static readonly ISet<string> Transactions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct FormatOption(string QueryValue, string MediaType, string Title);

    private static readonly ImmutableArray<FormatOption> _metadataFormats = ImmutableArray.Create(
        new FormatOption("json", MediaTypes.Json, "JSON"),
        new FormatOption("html", MediaTypes.Html, "HTML"));

    private static readonly ImmutableArray<FormatOption> _featureFormats = ImmutableArray.Create(
        new FormatOption("geojson", MediaTypes.GeoJson, "GeoJSON"),
        new FormatOption("json", MediaTypes.Json, "JSON"),
        new FormatOption("gml", MediaTypes.Gml, "GML"),
        new FormatOption("html", MediaTypes.Html, "HTML"));

    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _openApiCache =
        new(StringComparer.OrdinalIgnoreCase);

    private enum AxisOrder
    {
        EastNorth,
        NorthEast
    }

    private readonly record struct CrsDefinition(string Uri, int Srid, AxisOrder AxisOrder);

    private const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    private const string Epsg4326Uri = "http://www.opengis.net/def/crs/EPSG/0/4326";
    private const string WfsNamespace = "http://www.opengis.net/wfs/2.0";
    private const string GmlNamespace = "http://www.opengis.net/gml/3.2";
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private const string AtomNamespace = "http://www.w3.org/2005/Atom";

    private static BadRequest<string>? ValidateQueryParameters(HttpRequest request, ISet<string> allowedParameters)
    {
        foreach (var key in request.Query.Keys)
        {
            if (!allowedParameters.Contains(key))
            {
                return TypedResults.BadRequest($"Unknown query parameter: {key}");
            }
        }

        return null;
    }

    private static BadRequest<string>? ValidateItemsQueryParameters(
        HttpRequest request,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer)
    {
        var allowed = new HashSet<string>(AllowedQueryParameters.Items, StringComparer.OrdinalIgnoreCase);

        foreach (var field in layer.AttributeFields)
        {
            if (IsSimpleQueryableField(field))
            {
                allowed.Add(field.Name);
            }
        }

        foreach (var key in request.Query.Keys)
        {
            if (!allowed.Contains(key))
            {
                return TypedResults.BadRequest($"Unknown query parameter: {key}");
            }
        }

        return null;
    }

    private static bool IsSimpleQueryableField(Honua.Core.Features.Catalog.Domain.FieldDefinition field)
        => field.Type is Honua.Core.Features.Catalog.Domain.FieldType.String
            or Honua.Core.Features.Catalog.Domain.FieldType.Integer
            or Honua.Core.Features.Catalog.Domain.FieldType.BigInteger
            or Honua.Core.Features.Catalog.Domain.FieldType.Double
            or Honua.Core.Features.Catalog.Domain.FieldType.Float
            or Honua.Core.Features.Catalog.Domain.FieldType.Boolean
            or Honua.Core.Features.Catalog.Domain.FieldType.DateTime
            or Honua.Core.Features.Catalog.Domain.FieldType.Date
            or Honua.Core.Features.Catalog.Domain.FieldType.Time
            or Honua.Core.Features.Catalog.Domain.FieldType.Uuid;

    private static bool TryGetOutputFormat(
        string? formatParameter,
        HttpContext context,
        bool isFeatureContent,
        out string outputFormat,
        out IResult? error)
    {
        outputFormat = isFeatureContent ? MediaTypes.GeoJson : MediaTypes.Json;
        error = null;

        if (!string.IsNullOrWhiteSpace(formatParameter))
        {
            var normalized = formatParameter.Trim();
            switch (normalized.ToLowerInvariant())
            {
                case "json":
                    outputFormat = MediaTypes.Json;
                    return true;
                case "geojson" when isFeatureContent:
                    outputFormat = MediaTypes.GeoJson;
                    return true;
                case "geojson":
                    error = TypedResults.BadRequest("GeoJSON format is only supported for feature content");
                    return false;
                case "gml" when isFeatureContent:
                case "xml" when isFeatureContent:
                    outputFormat = MediaTypes.Gml;
                    return true;
                case "gml":
                case "xml":
                    error = TypedResults.BadRequest("GML format is only supported for feature content");
                    return false;
                case "html":
                    outputFormat = MediaTypes.Html;
                    return true;
                default:
                    error = TypedResults.BadRequest($"Unsupported format '{formatParameter}'");
                    return false;
            }
        }

        var acceptHeader = context.Request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        var acceptsGeoJson = acceptHeader.Contains("application/geo+json", StringComparison.OrdinalIgnoreCase);
        var acceptsJson = acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        var acceptsJsonSuffix = acceptHeader.Contains("+json", StringComparison.OrdinalIgnoreCase);
        var acceptsHtml = acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase);
        var acceptsGml = acceptHeader.Contains("application/gml+xml", StringComparison.OrdinalIgnoreCase) ||
                         acceptHeader.Contains("application/xml", StringComparison.OrdinalIgnoreCase) ||
                         acceptHeader.Contains("text/xml", StringComparison.OrdinalIgnoreCase) ||
                         acceptHeader.Contains("+xml", StringComparison.OrdinalIgnoreCase);

        if (isFeatureContent)
        {
            if (acceptsGml)
            {
                outputFormat = MediaTypes.Gml;
                return true;
            }

            if (acceptsGeoJson)
            {
                outputFormat = MediaTypes.GeoJson;
                return true;
            }

            if (acceptsJson || acceptsJsonSuffix)
            {
                outputFormat = MediaTypes.Json;
                return true;
            }

            if (acceptsHtml)
            {
                outputFormat = MediaTypes.Html;
                return true;
            }
        }
        else
        {
            if (acceptsJson || acceptsJsonSuffix)
            {
                outputFormat = MediaTypes.Json;
                return true;
            }

            if (acceptsHtml)
            {
                outputFormat = MediaTypes.Html;
                return true;
            }
        }

        if (acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = Results.StatusCode(StatusCodes.Status406NotAcceptable);
        return false;
    }

    private static string BuildUrlWithFormat(HttpRequest request, string basePath, string? formatValue)
    {
        var queryBuilder = new List<string>();

        foreach (var param in request.Query)
        {
            if (string.Equals(param.Key, "f", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(param.Value))
            {
                queryBuilder.Add($"{param.Key}={Uri.EscapeDataString(param.Value.ToString())}");
            }
        }

        if (!string.IsNullOrWhiteSpace(formatValue))
        {
            queryBuilder.Add($"f={Uri.EscapeDataString(formatValue)}");
        }

        return queryBuilder.Count > 0 ? $"{basePath}?{string.Join("&", queryBuilder)}" : basePath;
    }

    private static ImmutableArray<Link> BuildFormatLinks(
        HttpRequest request,
        string basePath,
        string outputFormat,
        ImmutableArray<FormatOption> formats,
        string title)
    {
        var links = new List<Link>
        {
            Link.Create(
                href: $"{basePath}{request.QueryString}",
                rel: RelationTypes.Self,
                type: outputFormat,
                title: title)
        };

        foreach (var format in formats)
        {
            if (string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(Link.Create(
                href: BuildUrlWithFormat(request, basePath, format.QueryValue),
                rel: RelationTypes.Alternate,
                type: format.MediaType,
                title: format.Title));
        }

        return links.ToImmutableArray();
    }

    private static ImmutableArray<Link> AddAlternateLinks(
        ImmutableArray<Link> existing,
        HttpRequest request,
        string basePath,
        string outputFormat,
        ImmutableArray<FormatOption> formats)
    {
        var builder = existing.ToBuilder();

        foreach (var format in formats)
        {
            if (string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            builder.Add(Link.Create(
                href: BuildUrlWithFormat(request, basePath, format.QueryValue),
                rel: RelationTypes.Alternate,
                type: format.MediaType,
                title: format.Title));
        }

        return builder.ToImmutableArray();
    }

    private static bool TryParseCrs(string? crsUri, HashSet<int> supportedSrids, out CrsDefinition crs, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(crsUri))
        {
            crs = new CrsDefinition(Crs84Uri, 4326, AxisOrder.EastNorth);
            return true;
        }

        if (string.Equals(crsUri, Crs84Uri, StringComparison.OrdinalIgnoreCase))
        {
            crs = new CrsDefinition(Crs84Uri, 4326, AxisOrder.EastNorth);
            return true;
        }

        if (TryParseEpsgUri(crsUri, out var srid))
        {
            if (!supportedSrids.Contains(srid))
            {
                error = $"Unsupported CRS '{crsUri}'. Supported CRS: {string.Join(", ", GetSupportedCrsUris(supportedSrids))}";
                crs = default;
                return false;
            }

            var axisOrder = srid == 4326 ? AxisOrder.NorthEast : AxisOrder.EastNorth;
            crs = new CrsDefinition(crsUri, srid, axisOrder);
            return true;
        }

        error = $"Unsupported CRS '{crsUri}'. Supported CRS: {string.Join(", ", GetSupportedCrsUris(supportedSrids))}";
        crs = default;
        return false;
    }

    private static ImmutableArray<string> GetSupportedCrsUris(HashSet<int> supportedSrids)
    {
        var uris = new List<string>
        {
            Crs84Uri,
            Epsg4326Uri
        };

        foreach (var srid in supportedSrids)
        {
            if (srid <= 0 || srid == 4326)
            {
                continue;
            }

            uris.Add($"http://www.opengis.net/def/crs/EPSG/0/{srid}");
        }

        return uris.ToImmutableArray();
    }

    private static bool TryParseEpsgUri(string crsUri, out int srid)
    {
        const string prefix = "http://www.opengis.net/def/crs/EPSG/0/";
        if (crsUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(crsUri[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out srid))
        {
            return true;
        }

        srid = 0;
        return false;
    }

    private static HashSet<int> BuildSupportedSrids(Honua.Core.Features.Catalog.Domain.LayerDefinition layer)
    {
        var srids = new HashSet<int> { 4326 };

        if (layer.SpatialReference.Srid > 0)
        {
            srids.Add(layer.SpatialReference.Srid);
        }

        return srids;
    }

    /// <summary>
    /// Handles the OGC API Features landing page request
    /// </summary>
    private static IResult HandleGetLandingPage(HttpContext context, string? f)
    {
        var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return formatError!;
        }

        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var basePath = $"{baseUrl}/ogc/features";

        var links = BuildFormatLinks(request, basePath, outputFormat, _metadataFormats, "This document")
            .ToBuilder();

        // API definition
        links.Add(Link.Create(
            href: $"{baseUrl}/openapi.json",
            rel: RelationTypes.ServiceDesc,
            type: MediaTypes.OpenApi,
            title: "API definition"));

        // Conformance declaration
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/conformance",
            rel: RelationTypes.Conformance,
            type: MediaTypes.Json,
            title: "Conformance declaration"));

        // Collections
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections",
            rel: RelationTypes.Data,
            type: MediaTypes.Json,
            title: "Feature collections"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Features",
            Description = "OGC API Features implementation for geospatial data access",
            Links = links.ToImmutable()
        };

        return FormatMetadataResponse(landingPage, OgcJsonContext.Default.LandingPage, outputFormat, "Landing page");
    }

    /// <summary>
    /// Handles the OGC API Features conformance declaration request
    /// </summary>
    private static IResult HandleGetConformance(HttpContext context, string? f)
    {
        var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return formatError!;
        }

        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                // OGC API Features Core
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",

                // OGC API Features CRS
                "http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs",

                // OGC API Features Filtering
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables-query-parameters",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter-cql2-text",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/filter-cql2-json",
                "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/features-filter",

                // OGC API Common
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections"
            ),
            Links = BuildFormatLinks(
                context.Request,
                $"{context.Request.Scheme}://{context.Request.Host}/ogc/features/conformance",
                outputFormat,
                _metadataFormats,
                "Conformance declaration")
        };

        return FormatMetadataResponse(conformance, OgcJsonContext.Default.ConformanceDeclaration, outputFormat, "Conformance");
    }

    /// <summary>
    /// Handles the OpenAPI 3.0 specification request using pre-generated static content for AOT compatibility
    /// </summary>
    private static async Task<IResult> HandleGetOpenApiSpec(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment)
    {
        var request = context.Request;

        var validationError = ValidateQueryParameters(request, AllowedQueryParameters.OpenApi);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "json", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.BadRequest($"Unsupported format '{f}'");
        }

        var acceptHeader = request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader) &&
            !acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/vnd.oai.openapi+json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("+json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status406NotAcceptable);
        }

        // Serve pre-generated OpenAPI spec for AOT compatibility
        // This avoids using Dictionary<string, object?> which is problematic for AOT compilation
        string? openApiContent = null;
        try
        {
            openApiContent = await GetOpenApiContentAsync(environment.ContentRootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            openApiContent = null;
        }

        if (!string.IsNullOrWhiteSpace(openApiContent))
        {
            return Results.Content(openApiContent, MediaTypes.OpenApi);
        }

        // Fallback: serve minimal spec if file not found
        const string fallbackSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Honua OGC API Features",
            "description": "OGC API Features implementation for geospatial data access",
            "version": "1.0.0"
          },
          "paths": {}
        }
        """;
        return Results.Content(fallbackSpec, MediaTypes.OpenApi);
    }

    private static Task<string?> GetOpenApiContentAsync(string contentRootPath)
    {
        var rootPath = Path.GetFullPath(contentRootPath);
        var cacheEntry = _openApiCache.GetOrAdd(rootPath, _ => new Lazy<Task<string?>>(
            () => ReadOpenApiContentAsync(rootPath)));
        return cacheEntry.Value;
    }

    private static async Task<string?> ReadOpenApiContentAsync(string contentRootPath)
    {
        var openApiPath = Path.Combine(contentRootPath, "openapi.json");
        if (!File.Exists(openApiPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(openApiPath);
    }

    /// <summary>
    /// Handles the OGC API Features collections list request
    /// </summary>
    private static async Task<IResult> HandleGetCollections(
        HttpContext context,
        string? f,
        ILayerCatalog layerCatalog,
        ILogger<OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = ValidateQueryParameters(request, AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

            var cancellationToken = GetTimeoutAwareCancellationToken(context);
            var layers = await layerCatalog.ListLayersAsync(cancellationToken);
            var collections = layers.Select(layer => CreateCollection(layer, baseUrl)).ToImmutableArray();

            var links = BuildFormatLinks(
                    request,
                    $"{baseUrl}/ogc/features/collections",
                    outputFormat,
                    _metadataFormats,
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

            return FormatMetadataResponse(response, OgcJsonContext.Default.Collections, outputFormat, "Collections");
        }
        catch (ArgumentException ex)
        {
            Log.InvalidCollectionsRequest(logger, ex);
            return TypedResults.BadRequest("Invalid request parameters.");
        }
        catch (InvalidOperationException ex)
        {
            Log.InvalidCollectionsOperation(logger, ex);
            return TypedResults.BadRequest("Invalid operation.");
        }
        catch (Exception ex)
        {
            Log.CollectionsQueryFailed(logger, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while retrieving collections.",
                statusCode: 500);
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
        ILogger<OgcFeaturesEndpointsLog> logger)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = ValidateQueryParameters(request, AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

            // OGC collection IDs are strings, but layer catalog uses int IDs
            // For now, try to parse the string as an integer
            if (!int.TryParse(collectionId, out var layerId))
            {
                return Results.NotFound();
            }

            var cancellationToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return Results.NotFound();
            }

            var collection = CreateCollection(layer, baseUrl);
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}";
            var selfHref = $"{basePath}{request.QueryString}";
            var updatedLinks = collection.Links.Select(link =>
                    string.Equals(link.Rel, RelationTypes.Self, StringComparison.OrdinalIgnoreCase)
                        ? link with { Href = selfHref, Type = outputFormat }
                        : link)
                .ToImmutableArray();

            updatedLinks = AddAlternateLinks(updatedLinks, request, basePath, outputFormat, _metadataFormats);
            collection = collection with { Links = updatedLinks };
            return FormatMetadataResponse(collection, OgcJsonContext.Default.CollectionInfo, outputFormat, "Collection");
        }
        catch (ArgumentException ex) when (ex.Message.Contains("parse") || ex.Message.Contains("invalid"))
        {
            Log.InvalidCollectionId(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid collection ID.");
        }
        catch (InvalidOperationException)
        {
            // Layer not found is a legitimate 404 case
            return TypedResults.NotFound();
        }
        catch (Exception ex)
        {
            Log.CollectionQueryFailed(logger, collectionId, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while retrieving the collection.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features queryables request
    /// Returns JSON Schema describing filterable properties for the collection
    /// </summary>
    private static async Task<IResult> HandleGetQueryables(
        string collectionId,
        string? f,
        HttpContext context,
        ILayerCatalog layerCatalog,
        ILogger<OgcFeaturesEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Verify collection/layer exists
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Build queryables schema from layer fields
            var queryables = CreateQueryablesSchema(layer);

            return FormatMetadataResponse(queryables, OgcJsonContext.Default.QueryablesSchema, outputFormat, "Queryables");
        }
        catch (ArgumentException ex) when (ex.Message.Contains("parse") || ex.Message.Contains("invalid"))
        {
            Log.InvalidCollectionId(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid collection ID.");
        }
        catch (InvalidOperationException)
        {
            // Layer not found is a legitimate 404 case
            return TypedResults.NotFound();
        }
        catch (Exception ex)
        {
            Log.CollectionQueryFailed(logger, collectionId, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while retrieving the queryables schema.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features items request with optional CQL filtering
    /// </summary>
    private static async Task<IResult> HandleGetItems(
        string collectionId,
        string? filter,
        string? bbox,
        string? crs,
        string? datetime,
        string? f,
        int? limit,
        int? offset,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IOptions<LimitsOptions> limitsOptions,
        IFeatureStore featureStore,
        [FromServices] ISqlFilterTranslator sqlFilterTranslator,
        ILogger<OgcFeaturesEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract hyphenated query parameters manually since minimal APIs don't support them directly
            var filterCrs = context.Request.Query["filter-crs"].FirstOrDefault();
            var bboxCrs = context.Request.Query["bbox-crs"].FirstOrDefault();

            if (!TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

            var filterLangRaw = context.Request.Query["filter-lang"].ToString();
            var filterLang = string.IsNullOrWhiteSpace(filterLangRaw) ? "cql2-text" : filterLangRaw;
            if (!string.Equals(filterLang, "cql2-text", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(filterLang, "cql2-json", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.BadRequest("Only filter-lang=cql2-text or cql2-json is supported");
            }

            if (string.IsNullOrWhiteSpace(filter) && !string.IsNullOrWhiteSpace(filterLangRaw))
            {
                return TypedResults.BadRequest("filter-lang requires a filter parameter");
            }

            // Validate limit and offset parameters
            if (limit.HasValue && limit.Value <= 0)
            {
                return TypedResults.BadRequest("Limit must be a positive integer");
            }

            if (offset.HasValue && offset.Value < 0)
            {
                return TypedResults.BadRequest("Offset must be non-negative");
            }

            var queryLimits = limitsOptions.Value.Query;

            if (limit.HasValue && limit.Value > queryLimits.MaxRecordCount)
            {
                return TypedResults.BadRequest($"Limit exceeds maximum of {queryLimits.MaxRecordCount}");
            }

            var effectiveLimit = limit ?? Math.Min(queryLimits.DefaultRecordCount, queryLimits.MaxRecordCount);
            var effectiveOffset = offset.HasValue ? Math.Min(offset.Value, queryLimits.MaxOffset) : 0;

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Verify collection/layer exists
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            var validationError = ValidateItemsQueryParameters(context.Request, layer);
            if (validationError is not null)
            {
                return validationError;
            }

            // Parse CQL filter if provided
            FilterExpression? filterExpression = null;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                try
                {
                    if (string.Equals(filterLang, "cql2-json", StringComparison.OrdinalIgnoreCase))
                    {
                        var parser = new Cql2JsonParser();
                        filterExpression = parser.Parse(filter);
                    }
                    else
                    {
                        var parser = new Cql2Parser();
                        filterExpression = parser.Parse(filter);
                    }
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest($"Invalid CQL filter: {ex.Message}");
                }
            }

            var supportedSrids = BuildSupportedSrids(layer);

            if (!TryParseCrs(crs, supportedSrids, out var outputCrs, out var crsError))
            {
                return TypedResults.BadRequest(crsError);
            }

            if (string.IsNullOrWhiteSpace(filter) && !string.IsNullOrWhiteSpace(filterCrs))
            {
                return TypedResults.BadRequest("filter-crs parameter requires a filter parameter");
            }

            var bboxCrsDefinition = new CrsDefinition(Crs84Uri, 4326, AxisOrder.EastNorth);
            if (!string.IsNullOrWhiteSpace(bbox))
            {
                if (!TryParseCrs(bboxCrs, supportedSrids, out bboxCrsDefinition, out var bboxCrsError))
                {
                    return TypedResults.BadRequest(bboxCrsError);
                }
            }

            CrsDefinition filterCrsDefinition = default;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (!TryParseCrs(filterCrs, supportedSrids, out filterCrsDefinition, out var filterCrsError))
                {
                    return TypedResults.BadRequest(filterCrsError);
                }
            }

            // Parse bbox parameter if provided (format: "minx,miny,maxx,maxy")
            SpatialFilter? spatialFilter = null;
            if (!string.IsNullOrWhiteSpace(bbox))
            {
                try
                {
                    spatialFilter = ParseBboxParameter(bbox, bboxCrsDefinition);
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest($"Invalid bbox parameter: {ex.Message}");
                }
            }

            // Parse datetime parameter if provided (ISO 8601 format or interval)
            TemporalFilter? temporalFilter = null;
            if (!TryBuildTemporalFilter(datetime, layer, out temporalFilter, out var temporalError))
            {
                return TypedResults.BadRequest(temporalError);
            }

            if (filterExpression != null)
            {
                filterExpression = ApplyFilterCrs(filterExpression, filterCrsDefinition);
            }

            var queryableFilter = BuildQueryableFilterExpression(context.Request.Query, layer, out var queryableError);
            if (queryableError is not null)
            {
                return TypedResults.BadRequest(queryableError);
            }

            if (queryableFilter != null)
            {
                filterExpression = filterExpression == null
                    ? queryableFilter
                    : new BinaryExpression(filterExpression, BinaryOperator.And, queryableFilter);
            }

            var queryableWhereClause = BuildQueryableWhereClause(context.Request.Query, layer);
            var whereClause = string.Equals(filterLang, "cql2-text", StringComparison.OrdinalIgnoreCase)
                ? BuildCombinedWhereClause(filter, queryableWhereClause)
                : queryableWhereClause;
            var includeNullGeometry = false;

            // Create feature query with proper parameterized filter support
            FeatureQuery featureQuery;
            if (filterExpression != null)
            {
                SqlFragment sqlFragment;
                try
                {
                    sqlFragment = sqlFilterTranslator.Translate(filterExpression, layer);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    return TypedResults.BadRequest($"Invalid CQL filter: {ex.Message}");
                }

                // Use parameterized SQL filter to preserve parameters
                featureQuery = new FeatureQuery
                {
                    Where = whereClause,
                    SqlFilter = sqlFragment,
                    SpatialFilter = spatialFilter,
                    TemporalFilter = temporalFilter,
                    IncludeNullGeometry = includeNullGeometry,
                    Limit = effectiveLimit,
                    Offset = effectiveOffset,
                    SpatialReferenceSrid = layer.SpatialReference.Srid,
                    OutputSrid = outputCrs.Srid
                };
            }
            else
            {
                featureQuery = new FeatureQuery
                {
                    Where = whereClause,
                    SpatialFilter = spatialFilter,
                    TemporalFilter = temporalFilter,
                    IncludeNullGeometry = includeNullGeometry,
                    Limit = effectiveLimit,
                    Offset = effectiveOffset,
                    SpatialReferenceSrid = layer.SpatialReference.Srid,
                    OutputSrid = outputCrs.Srid
                };
            }

            if (outputFormat == MediaTypes.Gml)
            {
                var gmlStopwatch = Stopwatch.StartNew();
                var gmlResult = await QueryGmlFeaturesAsync(featureStore, layerId, featureQuery, effectiveToken);
                gmlStopwatch.Stop();

                InfraLog.QueryExecuted(logger, collectionId, gmlResult.Items.Length, gmlStopwatch.Elapsed.TotalMilliseconds);

                if (spatialFilter != null && layer.HasGeometry)
                {
                    InfraLog.SpatialQueryExecuted(
                        logger,
                        collectionId,
                        spatialFilter.Value.SpatialRelationship.ToString(),
                        layer.GeometryType.ToString(),
                        gmlResult.Items.Length);
                }

                if (gmlResult.Items.Length >= queryLimits.MaxRecordCount)
                {
                    InfraLog.LargeResultSet(logger, collectionId, gmlResult.Items.Length);
                }

                return FormatGmlFeatureCollectionResponse(gmlResult, layer, context, collectionId, effectiveLimit, effectiveOffset, outputCrs);
            }

            // Query features
            var stopwatch = Stopwatch.StartNew();
            var result = await featureStore.QueryAsync(layerId, featureQuery, effectiveToken);
            stopwatch.Stop();

            InfraLog.QueryExecuted(logger, collectionId, result.Items.Length, stopwatch.Elapsed.TotalMilliseconds);

            if (spatialFilter != null && layer.HasGeometry)
            {
                InfraLog.SpatialQueryExecuted(
                    logger,
                    collectionId,
                    spatialFilter.Value.SpatialRelationship.ToString(),
                    layer.GeometryType.ToString(),
                    result.Items.Length);
            }

            if (result.Items.Length >= queryLimits.MaxRecordCount)
            {
                InfraLog.LargeResultSet(logger, collectionId, result.Items.Length);
            }

            // Convert to GeoJSON FeatureCollection with enhanced metadata
            var featureCollection = ConvertToGeoJsonFeatureCollection(
                result,
                context,
                collectionId,
                effectiveLimit,
                effectiveOffset,
                outputFormat,
                outputCrs.AxisOrder);

            // Return response in requested format
            return FormatFeatureCollectionResponse(featureCollection, outputFormat, outputCrs);
        }
        catch (ArgumentException ex)
        {
            // Client errors - invalid parameters, filters, etc.
            Log.InvalidItemsRequest(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid request.");
        }
        catch (InvalidOperationException ex)
        {
            // Client errors - invalid operations like layer not found
            Log.InvalidItemsOperation(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid operation.");
        }
        catch (Exception ex)
        {
            Log.ItemsQueryFailed(logger, collectionId, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while processing the request.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features single item request
    /// </summary>
    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string featureId,
        string? f,
        string? crs,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<OgcFeaturesEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Item);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Verify collection/layer exists
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            var supportedSrids = BuildSupportedSrids(layer);
            if (!TryParseCrs(crs, supportedSrids, out var outputCrs, out var crsError))
            {
                return TypedResults.BadRequest(crsError);
            }

            // Parse feature ID
            if (!long.TryParse(featureId, out var featureIdLong))
            {
                return TypedResults.NotFound();
            }

            var featureQuery = new FeatureQuery
            {
                SqlFilter = new SqlFragment("objectid = @p0", new object?[] { featureIdLong }),
                Where = $"objectid = {featureIdLong}",
                Limit = 1,
                SpatialReferenceSrid = layer.SpatialReference.Srid,
                OutputSrid = outputCrs.Srid
            };

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{featureId}";
            var itemLinksBuilder = BuildFormatLinks(
                    context.Request,
                    basePath,
                    outputFormat,
                    _featureFormats,
                    "This document")
                .ToBuilder();

            itemLinksBuilder.Add(Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                rel: RelationTypes.Collection,
                type: MediaTypes.Json,
                title: "Collection"));

            var itemLinks = itemLinksBuilder.ToImmutable();

            // Convert to GeoJSON feature
            if (outputFormat == MediaTypes.Gml)
            {
                var gmlResult = await QueryGmlFeaturesAsync(featureStore, layerId, featureQuery, effectiveToken);
                if (gmlResult.Items.IsDefaultOrEmpty)
                {
                    return TypedResults.NotFound();
                }

                var gmlFeature = gmlResult.Items[0];
                if (gmlFeature.Id != featureIdLong)
                {
                    return TypedResults.NotFound();
                }

                return FormatGmlFeatureResponse(gmlFeature, layer, collectionId, outputCrs, itemLinks);
            }

            var result = await featureStore.QueryAsync(layerId, featureQuery, effectiveToken);
            if (result.Items.IsDefaultOrEmpty)
            {
                return TypedResults.NotFound();
            }

            var feature = result.Items[0];
            if (feature.Id != featureIdLong)
            {
                return TypedResults.NotFound();
            }

            var geoJsonFeature = new GeoJsonFeature
            {
                Type = "Feature",
                Id = ((Feature)feature).Id,
                Geometry = ConvertFeatureGeometry(((Feature)feature).Geometry, outputCrs.AxisOrder),
                Properties = ((Feature)feature).Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Links = itemLinks
            };

            // Determine output format and return appropriate response
            return FormatFeatureResponse(geoJsonFeature, outputFormat, outputCrs);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidItemRequest(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid request.");
        }
        catch (InvalidOperationException ex)
        {
            Log.InvalidItemOperation(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid operation.");
        }
        catch (Exception ex)
        {
            Log.ItemQueryFailed(logger, collectionId, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while processing the request.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Parses OGC API Features bbox parameter into a SpatialFilter
    /// </summary>
    /// <param name="bbox">Bbox parameter in format "minx,miny,maxx,maxy" or "minx,miny,minz,maxx,maxy,maxz"</param>
    /// <param name="bboxCrs">CRS definition used to interpret bbox coordinates</param>
    /// <returns>SpatialFilter for bbox intersection queries</returns>
    private static SpatialFilter ParseBboxParameter(string bbox, CrsDefinition bboxCrs)
    {
        // Parse bbox format: "minx,miny,maxx,maxy" or "minx,miny,minz,maxx,maxy,maxz"
        var bboxParts = bbox.Split(',', StringSplitOptions.TrimEntries);
        if (bboxParts.Length is not (4 or 6))
        {
            throw new ArgumentException("Bbox parameter must contain exactly 4 or 6 comma-separated values");
        }

        double minx;
        double miny;
        double maxx;
        double maxy;
        double? minz = null;
        double? maxz = null;

        if (bboxParts.Length == 4)
        {
            if (!double.TryParse(bboxParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var first) ||
                !double.TryParse(bboxParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var second) ||
                !double.TryParse(bboxParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var third) ||
                !double.TryParse(bboxParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var fourth))
            {
                throw new ArgumentException("Bbox parameter values must be valid numbers");
            }

            if (bboxCrs.AxisOrder == AxisOrder.NorthEast)
            {
                miny = first;
                minx = second;
                maxy = third;
                maxx = fourth;
            }
            else
            {
                minx = first;
                miny = second;
                maxx = third;
                maxy = fourth;
            }
        }
        else
        {
            if (!double.TryParse(bboxParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var first) ||
                !double.TryParse(bboxParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var second) ||
                !double.TryParse(bboxParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMinz) ||
                !double.TryParse(bboxParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var third) ||
                !double.TryParse(bboxParts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var fourth) ||
                !double.TryParse(bboxParts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMaxz))
            {
                throw new ArgumentException("Bbox parameter values must be valid numbers");
            }

            minz = parsedMinz;
            maxz = parsedMaxz;

            if (bboxCrs.AxisOrder == AxisOrder.NorthEast)
            {
                miny = first;
                minx = second;
                maxy = third;
                maxx = fourth;
            }
            else
            {
                minx = first;
                miny = second;
                maxx = third;
                maxy = fourth;
            }
        }

        // Validate bbox bounds
        if (miny > maxy)
        {
            throw new ArgumentException("Invalid bbox: minimum latitude must be less than or equal to maximum latitude");
        }

        if (minz.HasValue && maxz.HasValue && minz.Value > maxz.Value)
        {
            throw new ArgumentException("Invalid bbox: minimum elevation must be less than or equal to maximum elevation");
        }

        // Validate coordinate ranges (assuming CRS84)
        if (bboxCrs.Srid == 4326 &&
            (minx < -180 || minx > 180 || maxx < -180 || maxx > 180 ||
             miny < -90 || miny > 90 || maxy < -90 || maxy > 90))
        {
            throw new ArgumentException("Bbox coordinates are out of valid range for WGS 84 (-180 to 180 longitude, -90 to 90 latitude)");
        }

        var crossesAntimeridian = minx > maxx;

        try
        {
            var wkbGeometry = crossesAntimeridian
                ? CreateAntimeridianBboxWkb(minx, miny, maxx, maxy, bboxCrs.Srid)
                : CreateBboxWkb(minx, miny, maxx, maxy, bboxCrs.Srid);

            return new SpatialFilter
            {
                Geometry = wkbGeometry,
                Srid = bboxCrs.Srid,
                SpatialRelationship = SpatialRelationship.Intersects
            };
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to create spatial filter from bbox: {ex.Message}", ex);
        }
    }

    private static byte[] CreateBboxWkb(double minx, double miny, double maxx, double maxy, int srid)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
        var envelope = new Envelope(minx, maxx, miny, maxy);
        var polygon = factory.ToGeometry(envelope);
        var writer = new WKBWriter();
        return writer.Write(polygon);
    }

    private static byte[] CreateAntimeridianBboxWkb(double minx, double miny, double maxx, double maxy, int srid)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
        var left = factory.ToGeometry(new Envelope(minx, 180, miny, maxy)) as Polygon
            ?? throw new ArgumentException("Failed to create antimeridian bbox polygon");
        var right = factory.ToGeometry(new Envelope(-180, maxx, miny, maxy)) as Polygon
            ?? throw new ArgumentException("Failed to create antimeridian bbox polygon");

        var multiPolygon = factory.CreateMultiPolygon(new[] { left, right });
        var writer = new WKBWriter();
        return writer.Write(multiPolygon);
    }

    private static FilterExpression ApplyFilterCrs(FilterExpression expression, CrsDefinition filterCrs)
    {
        return expression switch
        {
            SpatialPredicate spatial => new SpatialPredicate(
                spatial.Operator,
                ApplyFilterCrs(spatial.Left, filterCrs),
                ApplyFilterCrs(spatial.Right, filterCrs)),
            SpatialDistancePredicate spatial => new SpatialDistancePredicate(
                spatial.Operator,
                ApplyFilterCrs(spatial.Left, filterCrs),
                ApplyFilterCrs(spatial.Right, filterCrs),
                ApplyFilterCrs(spatial.Distance, filterCrs)),
            TemporalPredicate temporal => new TemporalPredicate(
                temporal.Operator,
                ApplyFilterCrs(temporal.Left, filterCrs),
                ApplyFilterCrs(temporal.Right, filterCrs)),
            ArrayPredicate array => new ArrayPredicate(
                array.Operator,
                ApplyFilterCrs(array.Left, filterCrs),
                ApplyFilterCrs(array.Right, filterCrs)),
            BinaryExpression binary => new BinaryExpression(
                ApplyFilterCrs(binary.Left, filterCrs),
                binary.Operator,
                ApplyFilterCrs(binary.Right, filterCrs)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                ApplyFilterCrs(unary.Operand, filterCrs)),
            FunctionCall function => new FunctionCall(
                function.FunctionName,
                function.Arguments.Select(arg => ApplyFilterCrs(arg, filterCrs)).ToArray()),
            ArrayLiteral arrayLiteral => new ArrayLiteral(
                arrayLiteral.Elements.Select(arg => ApplyFilterCrs(arg, filterCrs)).ToArray()),
            ValueList list => new ValueList(
                list.Values.Select(arg => ApplyFilterCrs(arg, filterCrs)).ToArray()),
            GeometryLiteral geometry => ApplyFilterCrs(geometry, filterCrs),
            IntervalLiteral interval => interval,
            _ => expression
        };
    }

    private static GeometryLiteral ApplyFilterCrs(GeometryLiteral geometry, CrsDefinition filterCrs)
    {
        var updatedWkb = geometry.Wkb;
        if (filterCrs.AxisOrder == AxisOrder.NorthEast)
        {
            updatedWkb = SwapAxisOrder(updatedWkb);
        }

        return new GeometryLiteral(updatedWkb, filterCrs.Srid, geometry.OriginalFormat);
    }

    private static byte[] SwapAxisOrder(byte[] wkb)
    {
        var reader = new WKBReader();
        var geometry = reader.Read(wkb);
        if (geometry == null)
        {
            return wkb;
        }

        geometry.Apply(new AxisOrderSwapFilter());
        geometry.GeometryChanged();

        var writer = new WKBWriter();
        return writer.Write(geometry);
    }

    private sealed class AxisOrderSwapFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;
        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence sequence, int i)
        {
            var x = sequence.GetX(i);
            var y = sequence.GetY(i);
            sequence.SetX(i, y);
            sequence.SetY(i, x);
        }
    }

    private static FilterExpression? BuildQueryableFilterExpression(
        IQueryCollection query,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        out string? error)
    {
        error = null;
        var queryableFields = layer.AttributeFields
            .Where(IsSimpleQueryableField)
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        FilterExpression? combined = null;

        foreach (var (key, values) in query)
        {
            if (!queryableFields.TryGetValue(key, out var field))
            {
                continue;
            }

            if (values.Count == 0)
            {
                continue;
            }

            var literals = new List<Literal>();
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = $"Queryable parameter '{field.Name}' must not be empty";
                    return null;
                }

                if (!TryParseQueryableLiteral(value, field.Type, out var literal, out var parseError))
                {
                    error = parseError;
                    return null;
                }

                literals.Add(literal);
            }

            FilterExpression expression = literals.Count == 1
                ? new BinaryExpression(new PropertyReference(field.Name), BinaryOperator.Equal, literals[0])
                : new BinaryExpression(new PropertyReference(field.Name), BinaryOperator.In, new ValueList(literals));

            combined = combined == null
                ? expression
                : new BinaryExpression(combined, BinaryOperator.And, expression);
        }

        return combined;
    }

    private static string? BuildQueryableWhereClause(
        IQueryCollection query,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer)
    {
        var queryableFields = layer.AttributeFields
            .Where(IsSimpleQueryableField)
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        var clauses = new List<string>();

        foreach (var (key, values) in query)
        {
            if (!queryableFields.TryGetValue(key, out var field))
            {
                continue;
            }

            var formattedValues = new List<string>();
            foreach (var value in values)
            {
                var rawValue = value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                if (!TryParseQueryableLiteral(rawValue, field.Type, out var literal, out _))
                {
                    continue;
                }

                formattedValues.Add(FormatQueryableLiteral(literal));
            }

            if (formattedValues.Count == 0)
            {
                continue;
            }

            if (formattedValues.Count == 1)
            {
                clauses.Add($"{field.Name} = {formattedValues[0]}");
            }
            else
            {
                clauses.Add($"{field.Name} IN ({string.Join(", ", formattedValues)})");
            }
        }

        return clauses.Count > 0 ? string.Join(" AND ", clauses) : null;
    }

    private static string? BuildCombinedWhereClause(string? filter, string? queryableWhereClause)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return string.IsNullOrWhiteSpace(queryableWhereClause) ? null : queryableWhereClause;
        }

        if (string.IsNullOrWhiteSpace(queryableWhereClause))
        {
            return filter;
        }

        return $"{filter} AND {queryableWhereClause}";
    }

    private static bool TryParseQueryableLiteral(
        string rawValue,
        Honua.Core.Features.Catalog.Domain.FieldType fieldType,
        out Literal literal,
        out string? error)
    {
        error = null;
        literal = null!;

        switch (fieldType)
        {
            case Honua.Core.Features.Catalog.Domain.FieldType.String:
                literal = new Literal(rawValue, LiteralType.Text);
                return true;
            case Honua.Core.Features.Catalog.Domain.FieldType.Integer:
            case Honua.Core.Features.Catalog.Domain.FieldType.BigInteger:
                if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    literal = new Literal(longValue, LiteralType.Number);
                    return true;
                }
                error = $"Queryable parameter value '{rawValue}' is not a valid integer";
                return false;
            case Honua.Core.Features.Catalog.Domain.FieldType.Double:
            case Honua.Core.Features.Catalog.Domain.FieldType.Float:
                if (double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    literal = new Literal(doubleValue, LiteralType.Number);
                    return true;
                }
                error = $"Queryable parameter value '{rawValue}' is not a valid number";
                return false;
            case Honua.Core.Features.Catalog.Domain.FieldType.Boolean:
                if (bool.TryParse(rawValue, out var boolValue))
                {
                    literal = new Literal(boolValue, LiteralType.Boolean);
                    return true;
                }
                if (rawValue is "0" or "1")
                {
                    literal = new Literal(rawValue == "1", LiteralType.Boolean);
                    return true;
                }
                error = $"Queryable parameter value '{rawValue}' is not a valid boolean";
                return false;
            case Honua.Core.Features.Catalog.Domain.FieldType.DateTime:
                if (DateTimeOffset.TryParse(
                    rawValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dateTimeOffset))
                {
                    literal = new Literal(dateTimeOffset, LiteralType.DateTime);
                    return true;
                }
                error = $"Queryable parameter value '{rawValue}' is not a valid date-time";
                return false;
            case Honua.Core.Features.Catalog.Domain.FieldType.Date:
                if (DateTime.TryParse(
                    rawValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dateValue))
                {
                    literal = new Literal(dateValue.Date, LiteralType.Date);
                    return true;
                }
                error = $"Queryable parameter value '{rawValue}' is not a valid date";
                return false;
            case Honua.Core.Features.Catalog.Domain.FieldType.Time:
                if (TimeSpan.TryParse(rawValue, CultureInfo.InvariantCulture, out var timeValue))
                {
                    literal = new Literal(timeValue, LiteralType.Text);
                    return true;
                }
                error = $"Queryable parameter value '{rawValue}' is not a valid time";
                return false;
            case Honua.Core.Features.Catalog.Domain.FieldType.Uuid:
                if (Guid.TryParse(rawValue, out var guidValue))
                {
                    literal = new Literal(guidValue, LiteralType.Text);
                    return true;
                }
                error = $"Queryable parameter value '{rawValue}' is not a valid UUID";
                return false;
            default:
                error = $"Queryable parameter value '{rawValue}' is not supported for type {fieldType}";
                return false;
        }
    }

    private static string FormatQueryableLiteral(Literal literal)
    {
        if (literal.Value == null)
        {
            return "NULL";
        }

        return literal.Type switch
        {
            LiteralType.Text => $"'{EscapeSqlLiteral(Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? string.Empty)}'",
            LiteralType.Boolean => literal.Value is bool boolValue && boolValue ? "TRUE" : "FALSE",
            LiteralType.Number => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "0",
            LiteralType.Date => $"'{FormatDateLiteral(literal.Value)}'",
            LiteralType.DateTime => $"'{FormatDateTimeLiteral(literal.Value)}'",
            _ => $"'{EscapeSqlLiteral(Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? string.Empty)}'"
        };
    }

    private static string FormatDateLiteral(object value)
    {
        return value switch
        {
            DateTimeOffset dto => XmlConvert.ToString(dto.UtcDateTime.Date, XmlDateTimeSerializationMode.Utc),
            DateTime dt => XmlConvert.ToString(dt.Date, XmlDateTimeSerializationMode.Utc),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static string FormatDateTimeLiteral(object value)
    {
        return value switch
        {
            DateTimeOffset dto => XmlConvert.ToString(dto.UtcDateTime, XmlDateTimeSerializationMode.Utc),
            DateTime dt => XmlConvert.ToString(dt, XmlDateTimeSerializationMode.Utc),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''");

    /// <summary>
    /// Converts query results to GeoJSON FeatureCollection
    /// </summary>
    private static FeatureCollection ConvertToGeoJsonFeatureCollection(
        QueryResult<Feature> queryResult,
        HttpContext httpContext,
        string collectionId,
        int? limit,
        int? offset,
        string outputFormat,
        AxisOrder axisOrder)
    {
        var features = queryResult.Items.Select(feature => new GeoJsonFeature
        {
            Type = "Feature",
            Id = ((Feature)feature).Id,
            Geometry = ConvertFeatureGeometry(((Feature)feature).Geometry, axisOrder),
            Properties = ((Feature)feature).Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        }).ToArray();

        // Generate paging links
        var links = GeneratePagingLinks(
            httpContext,
            collectionId,
            limit,
            offset,
            queryResult.TotalCount,
            queryResult.Items.Length,
            outputFormat,
            _featureFormats);

        return new FeatureCollection
        {
            Type = "FeatureCollection",
            Features = features,
            NumberMatched = queryResult.TotalCount,
            NumberReturned = queryResult.Items.Length,
            TimeStamp = DateTimeOffset.UtcNow,
            Links = links
        };
    }

    /// <summary>
    /// Generates paging links for OGC API Features responses
    /// </summary>
    private static ImmutableArray<Link> GeneratePagingLinks(
        HttpContext httpContext,
        string collectionId,
        int? requestedLimit,
        int? requestedOffset,
        long totalCount,
        int returnedCount,
        string outputFormat,
        ImmutableArray<FormatOption> formats)
    {
        var request = httpContext.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items";

        var links = new List<Link>();

        // Parse query parameters to preserve filters
        var includeFormatParam = request.Query.TryGetValue("f", out var formatParam);
        var queryParams = new Dictionary<string, string>();
        foreach (var param in request.Query)
        {
            if (param.Key != "limit" && param.Key != "offset" && !string.Equals(param.Key, "f", StringComparison.OrdinalIgnoreCase))
            {
                queryParams[param.Key] = param.Value.ToString();
            }
        }

        var effectiveLimit = requestedLimit ?? Math.Max(returnedCount, 1);
        var effectiveOffset = requestedOffset ?? 0;

        // Helper to build URL with query parameters
        string BuildUrl(int? limit, int? offset, string? formatValue)
        {
            var queryBuilder = new List<string>();

            foreach (var kvp in queryParams)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    queryBuilder.Add($"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}");
                }
            }

            if (!string.IsNullOrWhiteSpace(formatValue))
            {
                queryBuilder.Add($"f={Uri.EscapeDataString(formatValue)}");
            }

            if (limit.HasValue)
                queryBuilder.Add($"limit={limit}");

            if (offset.HasValue && offset > 0)
                queryBuilder.Add($"offset={offset}");

            return queryBuilder.Count > 0 ? $"{basePath}?{string.Join("&", queryBuilder)}" : basePath;
        }

        // Self link (current request)
        var selfFormat = includeFormatParam ? formatParam.ToString() : null;
        links.Add(Link.Create(
            href: BuildUrl(effectiveLimit, effectiveOffset, selfFormat),
            rel: RelationTypes.Self,
            type: outputFormat,
            title: "This document"
        ));

        foreach (var format in formats)
        {
            if (string.Equals(format.MediaType, outputFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, effectiveOffset, format.QueryValue),
                rel: RelationTypes.Alternate,
                type: format.MediaType,
                title: format.Title));
        }

        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{collectionId}",
            rel: RelationTypes.Collection,
            type: MediaTypes.Json,
            title: "Collection"));

        // Next link (if there are more results)
        var nextOffset = effectiveOffset + returnedCount;
        if (nextOffset < totalCount)
        {
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, nextOffset, selfFormat),
                rel: "next",
                type: outputFormat,
                title: "Next page"
            ));
        }

        // Previous link (if not on first page)
        if (effectiveOffset > 0)
        {
            var prevOffset = Math.Max(0, effectiveOffset - effectiveLimit);
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, prevOffset > 0 ? (int?)prevOffset : null, selfFormat),
                rel: "prev",
                type: outputFormat,
                title: "Previous page"
            ));
        }

        // First page link (if not on first page)
        if (effectiveOffset > 0)
        {
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, null, selfFormat), // offset=0 is implied when null
                rel: "first",
                type: outputFormat,
                title: "First page"
            ));
        }

        // Last page link (if not on last page and we know the total count)
        if (totalCount > 0 && nextOffset < totalCount)
        {
            // Calculate the offset for the last page
            var lastPageOffset = ((totalCount - 1) / effectiveLimit) * effectiveLimit;
            if (lastPageOffset != effectiveOffset)
            {
                links.Add(Link.Create(
                    href: BuildUrl(effectiveLimit, lastPageOffset > 0 ? (int?)lastPageOffset : null, selfFormat),
                    rel: "last",
                    type: outputFormat,
                    title: "Last page"
                ));
            }
        }

        return links.ToImmutableArray();
    }

    /// <summary>
    /// Converts a feature's WKB geometry to GeoJSON format
    /// </summary>
    private static SimpleGeoJsonGeometry? ConvertFeatureGeometry(byte[]? wkbGeometry, AxisOrder axisOrder = AxisOrder.EastNorth)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
        {
            return null;
        }

        try
        {
            var reader = new WKBReader();
            var geometry = reader.Read(wkbGeometry);

            return geometry == null ? null : BuildSimpleGeometry(geometry, axisOrder);
        }
        catch (Exception ex) when (ex is ArgumentException or ParseException or FormatException)
        {
            return null;
        }
    }

    private static SimpleGeoJsonGeometry? BuildSimpleGeometry(Geometry geometry, AxisOrder axisOrder)
    {
        if (geometry == null)
        {
            return null;
        }

        if (geometry is GeometryCollection collection)
        {
            return new SimpleGeoJsonGeometry
            {
                Type = "GeometryCollection",
                GeometriesJson = SerializeGeometryCollection(collection, axisOrder)
            };
        }

        return new SimpleGeoJsonGeometry
        {
            Type = MapGeometryType(geometry),
            CoordinatesJson = SerializeCoordinates(geometry, axisOrder)
        };
    }

    private static string MapGeometryType(Geometry geometry)
    {
        return geometry.OgcGeometryType switch
        {
            OgcGeometryType.Point => "Point",
            OgcGeometryType.LineString => "LineString",
            OgcGeometryType.Polygon => "Polygon",
            OgcGeometryType.MultiPoint => "MultiPoint",
            OgcGeometryType.MultiLineString => "MultiLineString",
            OgcGeometryType.MultiPolygon => "MultiPolygon",
            OgcGeometryType.GeometryCollection => "GeometryCollection",
            _ => geometry.GeometryType
        };
    }

    private static string? SerializeCoordinates(Geometry geometry, AxisOrder axisOrder)
    {
        if (geometry.IsEmpty)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        WriteCoordinates(writer, geometry, axisOrder);
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string SerializeGeometryCollection(GeometryCollection collection, AxisOrder axisOrder)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartArray();

        for (var i = 0; i < collection.NumGeometries; i++)
        {
            WriteGeometryObject(writer, collection.GetGeometryN(i), axisOrder);
        }

        writer.WriteEndArray();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteGeometryObject(Utf8JsonWriter writer, Geometry geometry, AxisOrder axisOrder)
    {
        writer.WriteStartObject();
        writer.WriteString("type", MapGeometryType(geometry));

        if (geometry is GeometryCollection collection)
        {
            writer.WritePropertyName("geometries");
            writer.WriteStartArray();

            for (var i = 0; i < collection.NumGeometries; i++)
            {
                WriteGeometryObject(writer, collection.GetGeometryN(i), axisOrder);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WritePropertyName("coordinates");

            if (geometry.IsEmpty)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteCoordinates(writer, geometry, axisOrder);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteCoordinates(Utf8JsonWriter writer, Geometry geometry, AxisOrder axisOrder)
    {
        switch (geometry)
        {
            case Point point:
                WritePointCoordinates(writer, point, axisOrder);
                break;
            case LineString lineString:
                WriteLineStringCoordinates(writer, lineString, axisOrder);
                break;
            case Polygon polygon:
                WritePolygonCoordinates(writer, polygon, axisOrder);
                break;
            case MultiPoint multiPoint:
                WriteMultiPointCoordinates(writer, multiPoint, axisOrder);
                break;
            case MultiLineString multiLineString:
                WriteMultiLineStringCoordinates(writer, multiLineString, axisOrder);
                break;
            case MultiPolygon multiPolygon:
                WriteMultiPolygonCoordinates(writer, multiPolygon, axisOrder);
                break;
            default:
                throw new ArgumentException($"Unsupported geometry type: {geometry.GeometryType}");
        }
    }

    private static void WritePointCoordinates(Utf8JsonWriter writer, Point point, AxisOrder axisOrder)
    {
        var sequence = point.CoordinateSequence;
        if (sequence.Count == 0)
        {
            writer.WriteNullValue();
            return;
        }

        WriteCoordinate(writer, sequence, 0, axisOrder);
    }

    private static void WriteLineStringCoordinates(Utf8JsonWriter writer, LineString lineString, AxisOrder axisOrder)
    {
        var sequence = lineString.CoordinateSequence;
        writer.WriteStartArray();
        for (var i = 0; i < sequence.Count; i++)
        {
            WriteCoordinate(writer, sequence, i, axisOrder);
        }
        writer.WriteEndArray();
    }

    private static void WritePolygonCoordinates(Utf8JsonWriter writer, Polygon polygon, AxisOrder axisOrder)
    {
        writer.WriteStartArray();
        WriteLineStringCoordinates(writer, polygon.ExteriorRing, axisOrder);
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            WriteLineStringCoordinates(writer, polygon.GetInteriorRingN(i), axisOrder);
        }
        writer.WriteEndArray();
    }

    private static void WriteMultiPointCoordinates(Utf8JsonWriter writer, MultiPoint multiPoint, AxisOrder axisOrder)
    {
        writer.WriteStartArray();
        for (var i = 0; i < multiPoint.NumGeometries; i++)
        {
            var point = (Point)multiPoint.GetGeometryN(i);
            if (point.IsEmpty || point.CoordinateSequence.Count == 0)
            {
                writer.WriteNullValue();
                continue;
            }

            WriteCoordinate(writer, point.CoordinateSequence, 0, axisOrder);
        }
        writer.WriteEndArray();
    }

    private static void WriteMultiLineStringCoordinates(Utf8JsonWriter writer, MultiLineString multiLineString, AxisOrder axisOrder)
    {
        writer.WriteStartArray();
        for (var i = 0; i < multiLineString.NumGeometries; i++)
        {
            WriteLineStringCoordinates(writer, (LineString)multiLineString.GetGeometryN(i), axisOrder);
        }
        writer.WriteEndArray();
    }

    private static void WriteMultiPolygonCoordinates(Utf8JsonWriter writer, MultiPolygon multiPolygon, AxisOrder axisOrder)
    {
        writer.WriteStartArray();
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            WritePolygonCoordinates(writer, (Polygon)multiPolygon.GetGeometryN(i), axisOrder);
        }
        writer.WriteEndArray();
    }

    private static void WriteCoordinate(Utf8JsonWriter writer, CoordinateSequence sequence, int index, AxisOrder axisOrder)
    {
        writer.WriteStartArray();
        if (axisOrder == AxisOrder.NorthEast)
        {
            writer.WriteNumberValue(sequence.GetY(index));
            writer.WriteNumberValue(sequence.GetX(index));
        }
        else
        {
            writer.WriteNumberValue(sequence.GetX(index));
            writer.WriteNumberValue(sequence.GetY(index));
        }

        if (sequence.Dimension > 2)
        {
            var z = sequence.GetOrdinate(index, Ordinate.Z);
            if (!double.IsNaN(z))
            {
                writer.WriteNumberValue(z);
            }
        }

        writer.WriteEndArray();
    }


    /// <summary>
    /// Creates queryables schema from layer definition
    /// Returns JSON Schema describing filterable properties for CQL2 queries
    /// </summary>
    private static QueryablesSchema CreateQueryablesSchema(Honua.Core.Features.Catalog.Domain.LayerDefinition layer)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, JsonSchemaProperty>();
        var requiredFields = new List<string>();

        // Add properties for all non-geometry fields
        foreach (var field in layer.AttributeFields.Where(IsSimpleQueryableField))
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
            // Geometry uses GeoJSON schema reference
            var geometryProperty = new JsonSchemaProperty
            {
                Type = "object", // GeoJSON geometry is an object
                Title = layer.GeometryField.DisplayName,
                Description = layer.GeometryField.Description ?? "Geometry property for spatial filtering",
                Ref = "https://geojson.org/schema/Geometry.json"
            };

            properties[layer.GeometryField.Name] = geometryProperty;

            // Add geometry field to required if not nullable
            if (!layer.GeometryField.Nullable)
            {
                requiredFields.Add(layer.GeometryField.Name);
            }
        }

        return new QueryablesSchema
        {
            Title = $"Queryable properties for {layer.Name}",
            Description = $"JSON Schema describing the queryable properties for collection '{layer.Name}'. " +
                         "These properties can be used in CQL2 filter expressions.",
            Properties = properties.ToImmutable(),
            Required = requiredFields.Count > 0 ? requiredFields.ToImmutableArray() : null
        };
    }

    /// <summary>
    /// Converts a FieldDefinition to a JSON Schema property
    /// </summary>
    private static JsonSchemaProperty ConvertFieldToJsonSchemaProperty(Honua.Core.Features.Catalog.Domain.FieldDefinition field)
    {
        var (jsonType, format) = GetJsonSchemaTypeAndFormat(field.Type);

        var property = new JsonSchemaProperty
        {
            Type = jsonType,
            Title = field.DisplayName,
            Description = field.Description,
            Format = format,
            Default = field.DefaultValue
        };

        // Add string-specific properties
        if (field.Type == Honua.Core.Features.Catalog.Domain.FieldType.String && field.Length.HasValue)
        {
            property = property with { MaxLength = field.Length.Value };
        }

        // Handle boolean fields that may use 0/1 encoding for GeoServices compatibility
        if (field.Type == Honua.Core.Features.Catalog.Domain.FieldType.Boolean)
        {
            property = property with { Enum = ImmutableArray.Create<object>(0, 1, false, true) };
        }

        return property;
    }

    /// <summary>
    /// Maps FieldType to JSON Schema type and format
    /// </summary>
    private static (string type, string? format) GetJsonSchemaTypeAndFormat(Honua.Core.Features.Catalog.Domain.FieldType fieldType)
    {
        return fieldType switch
        {
            Honua.Core.Features.Catalog.Domain.FieldType.String => ("string", null),
            Honua.Core.Features.Catalog.Domain.FieldType.Integer => ("integer", "int32"),
            Honua.Core.Features.Catalog.Domain.FieldType.BigInteger => ("integer", "int64"),
            Honua.Core.Features.Catalog.Domain.FieldType.Double => ("number", "double"),
            Honua.Core.Features.Catalog.Domain.FieldType.Float => ("number", "float"),
            Honua.Core.Features.Catalog.Domain.FieldType.Boolean => ("boolean", null),
            Honua.Core.Features.Catalog.Domain.FieldType.DateTime => ("string", "date-time"),
            Honua.Core.Features.Catalog.Domain.FieldType.Date => ("string", "date"),
            Honua.Core.Features.Catalog.Domain.FieldType.Time => ("string", "time"),
            Honua.Core.Features.Catalog.Domain.FieldType.Json => ("object", null), // JSONB as generic object
            Honua.Core.Features.Catalog.Domain.FieldType.Binary => ("string", "byte"), // Base64 encoded
            Honua.Core.Features.Catalog.Domain.FieldType.Uuid => ("string", "uuid"),
            Honua.Core.Features.Catalog.Domain.FieldType.Geometry => ("object", null), // Handled specially with $ref
            _ => ("string", null) // Fallback to string
        };
    }

    /// <summary>
    /// Converts a layer definition to OGC API Features collection
    /// </summary>
    private static CollectionInfo CreateCollection(
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer, string baseUrl)
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

            // Items link (for issue #17)
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/items",
                rel: RelationTypes.Items,
                type: MediaTypes.GeoJson,
                title: "Items"
            ),

            // Data link (OGC API Features requirement)
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

            // Queryables (OGC API Features Part 3)
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/queryables",
                rel: RelationTypes.Queryables,
                type: MediaTypes.Json,
                title: "Queryables"
            )
        );

        SpatialExtent? spatialExtent = null;
        if (layer.Extent is { } layerExtent)
        {
            var extentCrs = layerExtent.SpatialReference == 4326
                ? Crs84Uri
                : $"http://www.opengis.net/def/crs/EPSG/0/{layerExtent.SpatialReference}";
            spatialExtent = new SpatialExtent
            {
                BoundingBox = ImmutableArray.Create(
                    ImmutableArray.Create(layerExtent.MinX, layerExtent.MinY, layerExtent.MaxX, layerExtent.MaxY)
                ),
                Crs = extentCrs
            };
        }

        var extent = spatialExtent != null ? new Extent { Spatial = spatialExtent } : null;
        var supportedSrids = BuildSupportedSrids(layer);
        var crsUris = GetSupportedCrsUris(supportedSrids);

        return new CollectionInfo
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description,
            Links = collectionLinks,
            Extent = extent,
            ItemType = "feature",
            Crs = crsUris,
            StorageCrs = layer.SpatialReference.Srid > 0
                ? $"http://www.opengis.net/def/crs/EPSG/0/{layer.SpatialReference.Srid}"
                : null
        };
    }

    /// <summary>
    /// Handles the OGC API Features create feature request (POST)
    /// </summary>
    private static async Task<IResult> HandleCreateFeature(
        string collectionId,
        GeoJsonFeature feature,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<OgcFeaturesEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Transactions);
            if (validationError is not null)
            {
                return validationError;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Get layer information
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Validate feature data
            if (!string.Equals(feature.Type, "Feature", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.BadRequest("GeoJSON type must be 'Feature'");
            }

            // Convert GeoJSON feature to internal Feature format (ignore client-supplied ID)
            var internalFeature = ConvertGeoJsonFeatureToInternal(feature, ignoreId: true);

            // Create the feature
            var createdFeature = await featureStore.CreateAsync(layerId, internalFeature, effectiveToken);

            // Convert back to GeoJSON
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var responseFeature = new GeoJsonFeature
            {
                Type = "Feature",
                Id = createdFeature.Id,
                Geometry = ConvertFeatureGeometry(createdFeature.Geometry),
                Properties = createdFeature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Links = ImmutableArray.Create(
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}/items/{createdFeature.Id}",
                        rel: RelationTypes.Self,
                        type: MediaTypes.GeoJson,
                        title: "This document"
                    ),
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                        rel: RelationTypes.Collection,
                        type: MediaTypes.Json,
                        title: "Collection"
                    )
                )
            };

            // Return 201 Created with Location header
            context.Response.Headers.Location = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{responseFeature.Id}";
            return Results.Json(responseFeature, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidCreateRequest(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid request.");
        }
        catch (ResourceConflictException ex)
        {
            Log.InvalidCreateOperation(logger, collectionId, ex);
            return TypedResults.Conflict("Cannot create feature.");
        }
        catch (Exception ex)
        {
            Log.CreateFailed(logger, collectionId, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while creating the feature.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features update feature request (PUT)
    /// </summary>
    private static async Task<IResult> HandleUpdateFeature(
        string collectionId,
        string featureId,
        GeoJsonFeature feature,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<OgcFeaturesEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Transactions);
            if (validationError is not null)
            {
                return validationError;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Parse feature ID
            if (!long.TryParse(featureId, out var longFeatureId))
            {
                return TypedResults.NotFound();
            }

            // Get layer information
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Validate feature data
            if (!string.Equals(feature.Type, "Feature", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.BadRequest("GeoJSON type must be 'Feature'");
            }

            // Ensure the feature ID in the URL matches the feature data
            if (feature.Id.HasValue && feature.Id.Value != longFeatureId)
            {
                return TypedResults.BadRequest("Feature ID in URL does not match feature ID in data");
            }

            // Check if feature exists (no upsert behavior)
            var existingFeature = await featureStore.GetAsync(layerId, longFeatureId, effectiveToken);
            if (existingFeature == null)
            {
                return TypedResults.NotFound();
            }

            // Convert GeoJSON feature to internal Feature format using the path ID
            var internalFeature = ConvertGeoJsonFeatureToInternal(feature, overrideId: longFeatureId);

            // Update the feature
            var updatedFeature = await featureStore.UpdateAsync(layerId, internalFeature, effectiveToken);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var responseFeature = new GeoJsonFeature
            {
                Type = "Feature",
                Id = updatedFeature.Id,
                Geometry = ConvertFeatureGeometry(updatedFeature.Geometry),
                Properties = updatedFeature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Links = ImmutableArray.Create(
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}/items/{updatedFeature.Id}",
                        rel: RelationTypes.Self,
                        type: MediaTypes.GeoJson,
                        title: "This document"
                    ),
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                        rel: RelationTypes.Collection,
                        type: MediaTypes.Json,
                        title: "Collection"
                    )
                )
            };

            return Results.Json(responseFeature, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
        }
        catch (ArgumentException ex)
        {
            Log.InvalidUpdateRequest(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid request.");
        }
        catch (ResourceNotFoundException ex)
        {
            Log.InvalidUpdateOperation(logger, collectionId, ex);
            return TypedResults.NotFound();
        }
        catch (ResourceConflictException ex)
        {
            Log.InvalidUpdateOperation(logger, collectionId, ex);
            return TypedResults.Conflict("Cannot update feature.");
        }
        catch (InvalidOperationException ex)
        {
            Log.InvalidUpdateOperation(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid operation.");
        }
        catch (Exception ex)
        {
            Log.UpdateFailed(logger, collectionId, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while updating the feature.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features delete feature request (DELETE)
    /// </summary>
    private static async Task<IResult> HandleDeleteFeature(
        string collectionId,
        string featureId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger<OgcFeaturesEndpointsLog> logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Transactions);
            if (validationError is not null)
            {
                return validationError;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Parse feature ID
            if (!long.TryParse(featureId, out var longFeatureId))
            {
                return TypedResults.NotFound();
            }

            // Get layer information
            var effectiveToken = GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, effectiveToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Delete the feature
            var deleted = await featureStore.DeleteAsync(layerId, longFeatureId, effectiveToken);

            if (!deleted)
            {
                return TypedResults.NotFound();
            }

            // Return 204 No Content for successful deletion
            return Results.NoContent();
        }
        catch (ArgumentException ex)
        {
            Log.InvalidDeleteRequest(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid request.");
        }
        catch (InvalidOperationException ex)
        {
            Log.InvalidDeleteOperation(logger, collectionId, ex);
            return TypedResults.BadRequest("Invalid operation.");
        }
        catch (Exception ex)
        {
            Log.DeleteFailed(logger, collectionId, ex);
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while deleting the feature.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Converts a GeoJSON feature to internal Feature format
    /// </summary>
    private static Feature ConvertGeoJsonFeatureToInternal(
        GeoJsonFeature geoJsonFeature,
        long? overrideId = null,
        bool ignoreId = false)
    {
        byte[]? geometryWkb = null;
        if (geoJsonFeature.Geometry != null)
        {
            geometryWkb = ConvertGeoJsonGeometryToWkb(geoJsonFeature.Geometry);
        }

        var featureId = overrideId ?? (ignoreId ? 0 : ExtractFeatureId(geoJsonFeature));

        var attributes = geoJsonFeature.Properties?.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value) ?? ImmutableDictionary<string, object?>.Empty;

        return Feature.Create(featureId, geometryWkb, attributes);
    }

    private static long ExtractFeatureId(GeoJsonFeature geoJsonFeature)
    {
        if (!geoJsonFeature.Id.HasValue)
        {
            throw new ArgumentException("Feature ID is required");
        }

        return geoJsonFeature.Id.Value;
    }

    private static byte[] ConvertGeoJsonGeometryToWkb(SimpleGeoJsonGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(geometry.Type))
        {
            throw new ArgumentException("Geometry type is required");
        }

        var typeJson = $"\"{geometry.Type}\"";
        string geoJson;

        if (string.Equals(geometry.Type, "GeometryCollection", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(geometry.GeometriesJson))
            {
                throw new ArgumentException("GeometryCollection geometries are required");
            }

            geoJson = $"{{\"type\":{typeJson},\"geometries\":{geometry.GeometriesJson}}}";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(geometry.CoordinatesJson))
            {
                throw new ArgumentException("Geometry coordinates are required");
            }

            geoJson = $"{{\"type\":{typeJson},\"coordinates\":{geometry.CoordinatesJson}}}";
        }

        try
        {
            var reader = new GeoJsonReader();
            var parsed = reader.Read<Geometry>(geoJson)
                ?? throw new ArgumentException("Geometry could not be parsed");

            parsed.SRID = 4326;
            var (hasZ, hasM) = GetHasZandM(parsed);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: true, emitZ: hasZ, emitM: hasM);
            return writer.Write(parsed);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            throw new ArgumentException($"Invalid GeoJSON geometry: {ex.Message}", ex);
        }
    }

    private static (bool hasZ, bool hasM) GetHasZandM(Geometry geometry)
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

    private static bool TryBuildTemporalFilter(
        string? datetime,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        out TemporalFilter? temporalFilter,
        out string? error)
    {
        temporalFilter = null;
        error = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            return true;
        }

        if (!TryParseDatetimeParameter(datetime, out var start, out var end, out error))
        {
            return false;
        }

        var temporalField = layer.Fields.FirstOrDefault(field => field.Type == FieldType.DateTime)
            ?? layer.Fields.FirstOrDefault(field => field.Type == FieldType.Date);

        if (temporalField == null)
        {
            return true;
        }

        temporalFilter = new TemporalFilter
        {
            PropertyName = temporalField.Name,
            PropertyType = temporalField.Type == FieldType.Date ? TemporalPropertyType.Date : TemporalPropertyType.DateTime,
            Start = start,
            End = end
        };

        return true;
    }

    private static bool TryParseDatetimeParameter(
        string datetime,
        out DateTimeOffset? start,
        out DateTimeOffset? end,
        out string? error)
    {
        start = null;
        end = null;
        error = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            error = "Datetime parameter must not be empty";
            return false;
        }

        var parts = datetime.Split('/', StringSplitOptions.None);
        if (parts.Length == 1)
        {
            if (!TryParseDatetimeValue(parts[0], out var instant, out error))
            {
                return false;
            }

            start = instant;
            end = instant;
            return true;
        }

        if (parts.Length != 2)
        {
            error = "Datetime parameter must be a valid RFC 3339 timestamp or interval";
            return false;
        }

        if (!TryParseDatetimeValue(parts[0], out start, out error))
        {
            return false;
        }

        if (!TryParseDatetimeValue(parts[1], out end, out error))
        {
            return false;
        }

        if (!start.HasValue && !end.HasValue)
        {
            error = "Datetime interval must include a start or end value";
            return false;
        }

        if (start.HasValue && end.HasValue && start.Value > end.Value)
        {
            error = "Datetime interval start must be before end";
            return false;
        }

        return true;
    }

    private static bool TryParseDatetimeValue(
        string value,
        out DateTimeOffset? parsed,
        out string? error)
    {
        parsed = null;
        error = null;

        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == "..")
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsedValue))
        {
            error = $"Invalid datetime value '{value}'";
            return false;
        }

        parsed = parsedValue;
        return true;
    }

    private static IResult FormatMetadataResponse<T>(
        T payload,
        JsonTypeInfo<T> typeInfo,
        string outputFormat,
        string title)
    {
        if (outputFormat == MediaTypes.Html)
        {
            var json = JsonSerializer.Serialize(payload, typeInfo);
            var html = BuildHtmlDocument(title, json);
            return Results.Text(html, MediaTypes.Html);
        }

        return Results.Json(payload, typeInfo, contentType: MediaTypes.Json);
    }

    private readonly record struct GmlFeatureAdapter
    {
        public required long Id { get; init; }
        public required ImmutableDictionary<string, object?> Attributes { get; init; }
        public byte[]? Geometry { get; init; }
        public string? GeometryGml { get; init; }

        public static GmlFeatureAdapter FromFeature(Feature feature)
            => new()
            {
                Id = feature.Id,
                Attributes = feature.Attributes,
                Geometry = feature.Geometry,
                GeometryGml = null
            };

        public static GmlFeatureAdapter FromGmlFeature(GmlFeature feature)
            => new()
            {
                Id = feature.Id,
                Attributes = feature.Attributes,
                Geometry = null,
                GeometryGml = feature.GeometryGml
            };
    }

    private static async Task<QueryResult<GmlFeatureAdapter>> QueryGmlFeaturesAsync(
        IFeatureStore featureStore,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (featureStore is IGmlFeatureStore gmlFeatureStore)
        {
            var gmlResult = await gmlFeatureStore.QueryGmlAsync(layerId, query, cancellationToken);
            return AdaptGmlQueryResult(gmlResult);
        }

        var fallbackResult = await featureStore.QueryAsync(layerId, query, cancellationToken);
        return AdaptGmlQueryResult(fallbackResult);
    }

    private static QueryResult<GmlFeatureAdapter> AdaptGmlQueryResult(QueryResult<Feature> result)
    {
        if (result.Items.IsDefaultOrEmpty)
        {
            return QueryResult<GmlFeatureAdapter>.Create(
                result.TotalCount,
                ImmutableArray<GmlFeatureAdapter>.Empty,
                result.HasMoreResults);
        }

        var builder = ImmutableArray.CreateBuilder<GmlFeatureAdapter>(result.Items.Length);
        foreach (var feature in result.Items)
        {
            builder.Add(GmlFeatureAdapter.FromFeature(feature));
        }

        return QueryResult<GmlFeatureAdapter>.Create(result.TotalCount, builder.MoveToImmutable(), result.HasMoreResults);
    }

    private static QueryResult<GmlFeatureAdapter> AdaptGmlQueryResult(QueryResult<GmlFeature> result)
    {
        if (result.Items.IsDefaultOrEmpty)
        {
            return QueryResult<GmlFeatureAdapter>.Create(
                result.TotalCount,
                ImmutableArray<GmlFeatureAdapter>.Empty,
                result.HasMoreResults);
        }

        var builder = ImmutableArray.CreateBuilder<GmlFeatureAdapter>(result.Items.Length);
        foreach (var feature in result.Items)
        {
            builder.Add(GmlFeatureAdapter.FromGmlFeature(feature));
        }

        return QueryResult<GmlFeatureAdapter>.Create(result.TotalCount, builder.MoveToImmutable(), result.HasMoreResults);
    }

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    private static IResult FormatGmlFeatureCollectionResponse(
        QueryResult<GmlFeatureAdapter> queryResult,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        HttpContext httpContext,
        string collectionId,
        int? limit,
        int? offset,
        CrsDefinition crs)
    {
        var links = GeneratePagingLinks(
            httpContext,
            collectionId,
            limit,
            offset,
            queryResult.TotalCount,
            queryResult.Items.Length,
            MediaTypes.Gml,
            _featureFormats);

        return new ContentCrsResult(crs.Uri, () => new GmlFeatureCollectionResult(
            queryResult,
            layer,
            collectionId,
            links,
            crs));
    }

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    private static IResult FormatGmlFeatureResponse(
        GmlFeatureAdapter feature,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        string collectionId,
        CrsDefinition crs,
        ImmutableArray<Link>? links = null)
    {
        return new ContentCrsResult(crs.Uri, () => new GmlFeatureResult(
            feature,
            layer,
            collectionId,
            links ?? ImmutableArray<Link>.Empty,
            crs));
    }

    private sealed class GmlFeatureCollectionResult : IResult
    {
        private readonly QueryResult<GmlFeatureAdapter> _queryResult;
        private readonly Honua.Core.Features.Catalog.Domain.LayerDefinition _layer;
        private readonly string _collectionId;
        private readonly ImmutableArray<Link> _links;
        private readonly CrsDefinition _crs;

        public GmlFeatureCollectionResult(
            QueryResult<GmlFeatureAdapter> queryResult,
            Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
            string collectionId,
            ImmutableArray<Link> links,
            CrsDefinition crs)
        {
            _queryResult = queryResult;
            _layer = layer;
            _collectionId = collectionId;
            _links = links;
            _crs = crs;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = MediaTypes.Gml;

            var settings = new XmlWriterSettings
            {
                Async = true,
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = false
            };

            await using var writer = XmlWriter.Create(httpContext.Response.Body, settings);
            WriteGmlFeatureCollection(writer, _queryResult, _layer, _collectionId, _links, _crs);
            await writer.FlushAsync();
        }
    }

    private sealed class GmlFeatureResult : IResult
    {
        private readonly GmlFeatureAdapter _feature;
        private readonly Honua.Core.Features.Catalog.Domain.LayerDefinition _layer;
        private readonly string _collectionId;
        private readonly ImmutableArray<Link> _links;
        private readonly CrsDefinition _crs;

        public GmlFeatureResult(
            GmlFeatureAdapter feature,
            Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
            string collectionId,
            ImmutableArray<Link> links,
            CrsDefinition crs)
        {
            _feature = feature;
            _layer = layer;
            _collectionId = collectionId;
            _links = links;
            _crs = crs;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = MediaTypes.Gml;

            var settings = new XmlWriterSettings
            {
                Async = true,
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = false
            };

            await using var writer = XmlWriter.Create(httpContext.Response.Body, settings);
            WriteGmlFeature(writer, _feature, _layer, _collectionId, _links, _crs, includeNamespaces: true);
            await writer.FlushAsync();
        }
    }

    private static void WriteGmlFeatureCollection(
        XmlWriter writer,
        QueryResult<GmlFeatureAdapter> queryResult,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        string collectionId,
        ImmutableArray<Link> links,
        CrsDefinition crs)
    {
        var appNamespace = BuildAppNamespace(collectionId);

        writer.WriteStartDocument();
        writer.WriteStartElement("wfs", "FeatureCollection", WfsNamespace);
        writer.WriteAttributeString("xmlns", "wfs", null, WfsNamespace);
        writer.WriteAttributeString("xmlns", "gml", null, GmlNamespace);
        writer.WriteAttributeString("xmlns", "xsi", null, XsiNamespace);
        writer.WriteAttributeString("xmlns", "atom", null, AtomNamespace);
        writer.WriteAttributeString("xmlns", "app", null, appNamespace);
        writer.WriteAttributeString("timeStamp", XmlConvert.ToString(DateTimeOffset.UtcNow));
        writer.WriteAttributeString("numberMatched", queryResult.TotalCount.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("numberReturned", queryResult.Items.Length.ToString(CultureInfo.InvariantCulture));

        foreach (var link in links)
        {
            WriteAtomLink(writer, link);
        }

        foreach (var feature in queryResult.Items)
        {
            writer.WriteStartElement("wfs", "member", WfsNamespace);
            WriteGmlFeature(writer, feature, layer, collectionId, ImmutableArray<Link>.Empty, crs, includeNamespaces: false);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteGmlFeature(
        XmlWriter writer,
        GmlFeatureAdapter feature,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        string collectionId,
        ImmutableArray<Link> links,
        CrsDefinition crs,
        bool includeNamespaces)
    {
        var appNamespace = BuildAppNamespace(collectionId);
        var featureName = EncodeXmlName(layer.Name);

        writer.WriteStartElement("app", featureName, appNamespace);

        if (includeNamespaces)
        {
            writer.WriteAttributeString("xmlns", "app", null, appNamespace);
            writer.WriteAttributeString("xmlns", "gml", null, GmlNamespace);
            writer.WriteAttributeString("xmlns", "xsi", null, XsiNamespace);
            writer.WriteAttributeString("xmlns", "atom", null, AtomNamespace);
        }

        writer.WriteAttributeString("gml", "id", GmlNamespace, $"{collectionId}.{feature.Id}");

        var geometryField = layer.GeometryField;
        if (geometryField != null)
        {
            var geometryName = EncodeXmlName(geometryField.Name);
            writer.WriteStartElement("app", geometryName, appNamespace);
            WriteGmlGeometryValue(writer, feature.Geometry, feature.GeometryGml, crs);
            writer.WriteEndElement();
        }

        foreach (var (key, value) in feature.Attributes)
        {
            if (geometryField != null &&
                key.Equals(geometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var propertyName = EncodeXmlName(key);
            writer.WriteStartElement("app", propertyName, appNamespace);
            WriteGmlPropertyValue(writer, value, appNamespace);
            writer.WriteEndElement();
        }

        foreach (var link in links)
        {
            WriteAtomLink(writer, link);
        }

        writer.WriteEndElement();
    }

    private static void WriteAtomLink(XmlWriter writer, Link link)
    {
        writer.WriteStartElement("atom", "link", AtomNamespace);
        writer.WriteAttributeString("href", link.Href);

        if (!string.IsNullOrWhiteSpace(link.Rel))
        {
            writer.WriteAttributeString("rel", link.Rel);
        }

        if (!string.IsNullOrWhiteSpace(link.Type))
        {
            writer.WriteAttributeString("type", link.Type);
        }

        if (!string.IsNullOrWhiteSpace(link.Title))
        {
            writer.WriteAttributeString("title", link.Title);
        }

        if (!string.IsNullOrWhiteSpace(link.HrefLang))
        {
            writer.WriteAttributeString("hreflang", link.HrefLang);
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlGeometryValue(XmlWriter writer, byte[]? wkb, string? gml, CrsDefinition crs)
    {
        if (!string.IsNullOrWhiteSpace(gml))
        {
            writer.WriteRaw(gml);
            return;
        }

        WriteGmlGeometryValue(writer, wkb, crs);
    }

    private static void WriteGmlGeometryValue(XmlWriter writer, byte[]? wkb, CrsDefinition crs)
    {
        if (wkb == null || wkb.Length == 0)
        {
            writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
            return;
        }

        try
        {
            var reader = new WKBReader();
            var geometry = reader.Read(wkb);
            if (geometry == null)
            {
                writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
                return;
            }

            WriteGmlGeometry(writer, geometry, crs);
        }
        catch (Exception)
        {
            writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
        }
    }

    private static void WriteGmlGeometry(XmlWriter writer, Geometry geometry, CrsDefinition crs)
    {
        switch (geometry)
        {
            case Point point:
                WriteGmlPoint(writer, point, crs);
                break;
            case LineString lineString:
                WriteGmlLineString(writer, lineString, crs);
                break;
            case Polygon polygon:
                WriteGmlPolygon(writer, polygon, crs);
                break;
            case MultiPoint multiPoint:
                WriteGmlMultiPoint(writer, multiPoint, crs);
                break;
            case MultiLineString multiLineString:
                WriteGmlMultiLineString(writer, multiLineString, crs);
                break;
            case MultiPolygon multiPolygon:
                WriteGmlMultiPolygon(writer, multiPolygon, crs);
                break;
            case GeometryCollection collection:
                WriteGmlGeometryCollection(writer, collection, crs);
                break;
            default:
                throw new ArgumentException($"Unsupported geometry type: {geometry.GeometryType}");
        }
    }

    private static void WriteGmlPoint(XmlWriter writer, Point point, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "Point", GmlNamespace);
        writer.WriteAttributeString("srsName", crs.Uri);

        if (!point.IsEmpty && point.CoordinateSequence.Count > 0)
        {
            writer.WriteStartElement("gml", "pos", GmlNamespace);
            writer.WriteString(FormatCoordinate(point.CoordinateSequence, 0, crs.AxisOrder));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlLineString(XmlWriter writer, LineString lineString, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "LineString", GmlNamespace);
        writer.WriteAttributeString("srsName", crs.Uri);

        if (!lineString.IsEmpty)
        {
            writer.WriteStartElement("gml", "posList", GmlNamespace);
            writer.WriteString(FormatPosList(lineString.CoordinateSequence, crs.AxisOrder));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlPolygon(XmlWriter writer, Polygon polygon, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "Polygon", GmlNamespace);
        writer.WriteAttributeString("srsName", crs.Uri);

        if (!polygon.IsEmpty)
        {
            writer.WriteStartElement("gml", "exterior", GmlNamespace);
            WriteGmlLinearRing(writer, polygon.ExteriorRing, crs);
            writer.WriteEndElement();

            for (var i = 0; i < polygon.NumInteriorRings; i++)
            {
                writer.WriteStartElement("gml", "interior", GmlNamespace);
                WriteGmlLinearRing(writer, polygon.GetInteriorRingN(i), crs);
                writer.WriteEndElement();
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlLinearRing(XmlWriter writer, LineString ring, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "LinearRing", GmlNamespace);

        if (!ring.IsEmpty)
        {
            writer.WriteStartElement("gml", "posList", GmlNamespace);
            writer.WriteString(FormatPosList(ring.CoordinateSequence, crs.AxisOrder));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlMultiPoint(XmlWriter writer, MultiPoint multiPoint, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "MultiPoint", GmlNamespace);
        writer.WriteAttributeString("srsName", crs.Uri);

        for (var i = 0; i < multiPoint.NumGeometries; i++)
        {
            writer.WriteStartElement("gml", "pointMember", GmlNamespace);
            WriteGmlPoint(writer, (Point)multiPoint.GetGeometryN(i), crs);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlMultiLineString(XmlWriter writer, MultiLineString multiLineString, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "MultiCurve", GmlNamespace);
        writer.WriteAttributeString("srsName", crs.Uri);

        for (var i = 0; i < multiLineString.NumGeometries; i++)
        {
            writer.WriteStartElement("gml", "curveMember", GmlNamespace);
            WriteGmlLineString(writer, (LineString)multiLineString.GetGeometryN(i), crs);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlMultiPolygon(XmlWriter writer, MultiPolygon multiPolygon, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "MultiSurface", GmlNamespace);
        writer.WriteAttributeString("srsName", crs.Uri);

        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            writer.WriteStartElement("gml", "surfaceMember", GmlNamespace);
            WriteGmlPolygon(writer, (Polygon)multiPolygon.GetGeometryN(i), crs);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteGmlGeometryCollection(XmlWriter writer, GeometryCollection collection, CrsDefinition crs)
    {
        writer.WriteStartElement("gml", "MultiGeometry", GmlNamespace);
        writer.WriteAttributeString("srsName", crs.Uri);

        for (var i = 0; i < collection.NumGeometries; i++)
        {
            writer.WriteStartElement("gml", "geometryMember", GmlNamespace);
            WriteGmlGeometry(writer, collection.GetGeometryN(i), crs);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string FormatPosList(CoordinateSequence sequence, AxisOrder axisOrder)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < sequence.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(FormatCoordinate(sequence, i, axisOrder));
        }

        return builder.ToString();
    }

    private static string FormatCoordinate(CoordinateSequence sequence, int index, AxisOrder axisOrder)
    {
        var x = sequence.GetX(index);
        var y = sequence.GetY(index);
        var hasZ = sequence.Dimension > 2;
        var z = hasZ ? sequence.GetOrdinate(index, Ordinate.Z) : double.NaN;

        var builder = new StringBuilder();
        if (axisOrder == AxisOrder.NorthEast)
        {
            builder.AppendFormat(CultureInfo.InvariantCulture, "{0} {1}", y, x);
        }
        else
        {
            builder.AppendFormat(CultureInfo.InvariantCulture, "{0} {1}", x, y);
        }

        if (hasZ && !double.IsNaN(z))
        {
            builder.AppendFormat(CultureInfo.InvariantCulture, " {0}", z);
        }

        return builder.ToString();
    }

    private static void WriteGmlPropertyValue(XmlWriter writer, object? value, string appNamespace)
    {
        if (value == null)
        {
            writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
            return;
        }

        switch (value)
        {
            case string text:
                writer.WriteString(text);
                break;
            case bool boolValue:
                writer.WriteString(boolValue ? "true" : "false");
                break;
            case DateTime dateTime:
                writer.WriteString(XmlConvert.ToString(dateTime, XmlDateTimeSerializationMode.Utc));
                break;
            case DateTimeOffset dateTimeOffset:
                writer.WriteString(XmlConvert.ToString(dateTimeOffset.UtcDateTime, XmlDateTimeSerializationMode.Utc));
                break;
            case Guid guid:
                writer.WriteString(guid.ToString());
                break;
            case JsonElement jsonElement:
                WriteJsonElementValue(writer, jsonElement, appNamespace);
                break;
            case IFormattable formattable:
                writer.WriteString(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private static void WriteJsonElementValue(XmlWriter writer, JsonElement element, string appNamespace)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteString(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var longValue))
                {
                    writer.WriteString(longValue.ToString(CultureInfo.InvariantCulture));
                }
                else if (element.TryGetDouble(out var doubleValue))
                {
                    writer.WriteString(doubleValue.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteString(element.GetRawText());
                }
                break;
            case JsonValueKind.True:
                writer.WriteString("true");
                break;
            case JsonValueKind.False:
                writer.WriteString("false");
                break;
            case JsonValueKind.Null:
                writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = EncodeXmlName(property.Name);
                    writer.WriteStartElement("app", name, appNamespace);
                    WriteJsonElementValue(writer, property.Value, appNamespace);
                    writer.WriteEndElement();
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    writer.WriteStartElement("app", "member", appNamespace);
                    WriteJsonElementValue(writer, item, appNamespace);
                    writer.WriteEndElement();
                }
                break;
            default:
                writer.WriteString(element.GetRawText());
                break;
        }
    }

    private static string BuildAppNamespace(string collectionId)
        => $"http://www.opengis.net/ogcapi/features/collections/{collectionId}";

    private static string EncodeXmlName(string value)
        => XmlConvert.EncodeName(value);

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    private static IResult FormatFeatureCollectionResponse(FeatureCollection featureCollection, string outputFormat, CrsDefinition crs)
    {
        return new ContentCrsResult(crs.Uri, () =>
        {
            if (outputFormat == MediaTypes.Html)
            {
                var json = JsonSerializer.Serialize(featureCollection, OgcJsonContext.Default.FeatureCollection);
                var html = BuildHtmlDocument("Feature collection", json);
                return Results.Text(html, MediaTypes.Html);
            }

            var contentType = outputFormat == MediaTypes.Json ? MediaTypes.Json : MediaTypes.GeoJson;
            return Results.Json(featureCollection, OgcJsonContext.Default.FeatureCollection, contentType: contentType);
        });
    }

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    private static IResult FormatFeatureResponse(GeoJsonFeature geoJsonFeature, string outputFormat, CrsDefinition crs)
    {
        return new ContentCrsResult(crs.Uri, () =>
        {
            if (outputFormat == MediaTypes.Html)
            {
                var json = JsonSerializer.Serialize(geoJsonFeature, OgcJsonContext.Default.GeoJsonFeature);
                var html = BuildHtmlDocument("Feature", json);
                return Results.Text(html, MediaTypes.Html);
            }

            var contentType = outputFormat == MediaTypes.Json ? MediaTypes.Json : MediaTypes.GeoJson;
            return Results.Json(geoJsonFeature, OgcJsonContext.Default.GeoJsonFeature, contentType: contentType);
        });
    }


    /// <summary>
    /// Custom IResult implementation that adds Content-Crs header to geometry responses
    /// as required by OGC API Features Part 2
    /// </summary>
    private sealed class ContentCrsResult : IResult
    {
        private readonly string _crs;
        private readonly Func<IResult> _resultFactory;

        public ContentCrsResult(string crs, Func<IResult> resultFactory)
        {
            _crs = crs;
            _resultFactory = resultFactory;
        }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            // Add Content-Crs header before executing the underlying result
            httpContext.Response.Headers.Append("Content-Crs", $"<{_crs}>");

            // Execute the underlying result
            var result = _resultFactory();
            await result.ExecuteAsync(httpContext);
        }
    }

    private static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 3100, Level = LogLevel.Warning, Message = "Invalid OGC collections request.")]
        public static partial void InvalidCollectionsRequest(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3101, Level = LogLevel.Warning, Message = "Invalid OGC collections operation.")]
        public static partial void InvalidCollectionsOperation(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3102, Level = LogLevel.Error, Message = "Error retrieving OGC collections.")]
        public static partial void CollectionsQueryFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "Invalid OGC collection ID {CollectionId}.")]
        public static partial void InvalidCollectionId(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3104, Level = LogLevel.Error, Message = "Error retrieving OGC collection {CollectionId}.")]
        public static partial void CollectionQueryFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3105, Level = LogLevel.Warning, Message = "Invalid OGC items request for collection {CollectionId}.")]
        public static partial void InvalidItemsRequest(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3106, Level = LogLevel.Warning, Message = "Invalid OGC items operation for collection {CollectionId}.")]
        public static partial void InvalidItemsOperation(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3107, Level = LogLevel.Error, Message = "Error processing OGC items request for collection {CollectionId}.")]
        public static partial void ItemsQueryFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3108, Level = LogLevel.Warning, Message = "Invalid OGC item request for collection {CollectionId}.")]
        public static partial void InvalidItemRequest(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3109, Level = LogLevel.Warning, Message = "Invalid OGC item operation for collection {CollectionId}.")]
        public static partial void InvalidItemOperation(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3110, Level = LogLevel.Error, Message = "Error processing OGC item request for collection {CollectionId}.")]
        public static partial void ItemQueryFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3111, Level = LogLevel.Warning, Message = "Invalid OGC create feature request for collection {CollectionId}.")]
        public static partial void InvalidCreateRequest(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3112, Level = LogLevel.Warning, Message = "Invalid OGC create feature operation for collection {CollectionId}.")]
        public static partial void InvalidCreateOperation(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3113, Level = LogLevel.Error, Message = "Error creating OGC feature for collection {CollectionId}.")]
        public static partial void CreateFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3114, Level = LogLevel.Warning, Message = "Invalid OGC update request for collection {CollectionId}.")]
        public static partial void InvalidUpdateRequest(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3115, Level = LogLevel.Warning, Message = "Invalid OGC update operation for collection {CollectionId}.")]
        public static partial void InvalidUpdateOperation(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3116, Level = LogLevel.Error, Message = "Error updating OGC feature for collection {CollectionId}.")]
        public static partial void UpdateFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3117, Level = LogLevel.Warning, Message = "Invalid OGC delete request for collection {CollectionId}.")]
        public static partial void InvalidDeleteRequest(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3118, Level = LogLevel.Warning, Message = "Invalid OGC delete operation for collection {CollectionId}.")]
        public static partial void InvalidDeleteOperation(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 3119, Level = LogLevel.Error, Message = "Error deleting OGC feature for collection {CollectionId}.")]
        public static partial void DeleteFailed(ILogger logger, string collectionId, Exception exception);
    }

    private static string BuildHtmlDocument(string title, string json)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedJson = WebUtility.HtmlEncode(json);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>{encodedTitle}</title>
<style>
body {{ font-family: Arial, sans-serif; margin: 24px; color: #111; background: #f8f8f8; }}
main {{ max-width: 1200px; margin: 0 auto; background: #fff; padding: 16px 20px; border: 1px solid #ddd; }}
h1 {{ font-size: 20px; margin: 0 0 12px; }}
pre {{ white-space: pre-wrap; word-break: break-word; margin: 0; }}
</style>
</head>
<body>
<main>
<h1>{encodedTitle}</h1>
<pre>{encodedJson}</pre>
</main>
</body>
</html>";
    }
}
