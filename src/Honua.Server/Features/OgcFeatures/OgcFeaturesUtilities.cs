// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Shared utilities and constants for OGC API Features endpoints
/// </summary>
internal static class OgcFeaturesUtilities
{
    /// <summary>
    /// Allowed query parameters by operation type
    /// </summary>
    public static class AllowedQueryParameters
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

    /// <summary>
    /// Format option for content negotiation
    /// </summary>
    public readonly record struct FormatOption(string QueryValue, string MediaType, string Title);

    /// <summary>
    /// CRS definition with URI, SRID, and axis order
    /// </summary>
    public readonly record struct CrsDefinition(string Uri, int Srid, AxisOrder AxisOrder);

    /// <summary>
    /// Axis order enumeration
    /// </summary>
    public enum AxisOrder
    {
        EastNorth,
        NorthEast
    }

    /// <summary>
    /// Supported metadata formats
    /// </summary>
    public static readonly ImmutableArray<FormatOption> MetadataFormats = ImmutableArray.Create(
        new FormatOption("json", MediaTypes.Json, "JSON"),
        new FormatOption("html", MediaTypes.Html, "HTML"));

    /// <summary>
    /// Supported feature formats
    /// </summary>
    public static readonly ImmutableArray<FormatOption> FeatureFormats = ImmutableArray.Create(
        new FormatOption("geojson", MediaTypes.GeoJson, "GeoJSON"),
        new FormatOption("json", MediaTypes.Json, "JSON"),
        new FormatOption("gml", MediaTypes.Gml, "GML"),
        new FormatOption("html", MediaTypes.Html, "HTML"));

    // CRS constants
    public const string Crs84Uri = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
    public const string Epsg4326Uri = "http://www.opengis.net/def/crs/EPSG/0/4326";
    public const string WfsNamespace = "http://www.opengis.net/wfs/2.0";
    public const string GmlNamespace = "http://www.opengis.net/gml/3.2";
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    public const string AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <summary>
    /// Validates query parameters against allowed set
    /// </summary>
    public static BadRequest<string>? ValidateQueryParameters(HttpRequest request, ISet<string> allowedParameters)
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

    /// <summary>
    /// Validates query parameters for items requests including queryable fields
    /// </summary>
    public static BadRequest<string>? ValidateItemsQueryParameters(
        HttpRequest request,
        LayerDefinition layer)
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

    /// <summary>
    /// Determines if a field is queryable for simple parameter filtering
    /// </summary>
    public static bool IsSimpleQueryableField(FieldDefinition field)
        => field.Type is FieldType.String
            or FieldType.Integer
            or FieldType.BigInteger
            or FieldType.Double
            or FieldType.Float
            or FieldType.Boolean
            or FieldType.DateTime
            or FieldType.Date
            or FieldType.Time
            or FieldType.Uuid;

    /// <summary>
    /// Determines output format from request parameters and headers
    /// </summary>
    public static bool TryGetOutputFormat(
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

    /// <summary>
    /// Builds URL with format parameter
    /// </summary>
    public static string BuildUrlWithFormat(HttpRequest request, string basePath, string? formatValue)
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

    /// <summary>
    /// Builds format-specific links for content negotiation
    /// </summary>
    public static ImmutableArray<Link> BuildFormatLinks(
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

    /// <summary>
    /// Adds alternate format links to existing link collection
    /// </summary>
    public static ImmutableArray<Link> AddAlternateLinks(
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

    /// <summary>
    /// Formats metadata response with HTML option
    /// </summary>
    public static IResult FormatMetadataResponse<T>(
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

    /// <summary>
    /// Builds HTML document wrapper for JSON content
    /// </summary>
    private static string BuildHtmlDocument(string title, string json)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>{title}</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; }}
        pre {{ background: #f5f5f5; padding: 20px; border-radius: 5px; overflow: auto; }}
        .title {{ color: #333; margin-bottom: 20px; }}
    </style>
</head>
<body>
    <h1 class=""title"">{title}</h1>
    <pre><code>{json}</code></pre>
</body>
</html>";
    }
}
