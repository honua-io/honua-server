// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// Format option for content negotiation.
/// </summary>
internal readonly record struct FormatOption(string QueryValue, string MediaType, string Title);

/// <summary>
/// Shared utilities for OGC API responses.
/// </summary>
internal static class OgcCommonUtilities
{
    private static readonly IReadOnlyDictionary<string, string> _featureFormatParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["json"] = MediaTypes.Json,
            ["geojson"] = MediaTypes.GeoJson,
            ["gml"] = MediaTypes.Gml,
            ["xml"] = MediaTypes.Gml,
            ["csv"] = MediaTypes.Csv,
            ["html"] = MediaTypes.Html
        };

    private static readonly IReadOnlyDictionary<string, string> _metadataFormatParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["json"] = MediaTypes.Json,
            ["html"] = MediaTypes.Html
        };

    private static readonly string[] _featureSupportedMediaTypes =
    [
        MediaTypes.GeoJson,
        MediaTypes.Json,
        MediaTypes.Gml,
        MediaTypes.Csv,
        MediaTypes.Html
    ];

    private static readonly string[] _metadataSupportedMediaTypes =
    [
        MediaTypes.Json,
        MediaTypes.Html
    ];

    /// <summary>
    /// Supported metadata formats.
    /// </summary>
    public static readonly ImmutableArray<FormatOption> MetadataFormats = ImmutableArray.Create(
        new FormatOption("json", MediaTypes.Json, "JSON"),
        new FormatOption("html", MediaTypes.Html, "HTML"));

    /// <summary>
    /// Validates query parameters against allowed set.
    /// </summary>
    public static BadRequest<string>? ValidateQueryParameters(HttpRequest request, IReadOnlySet<string> allowedParameters)
    {
        var validator = request.HttpContext.RequestServices.GetRequiredService<ICommonQueryValidator>();
        var error = QueryParameterValidationHelpers.GetValidationError(
            validator,
            request.Query.Keys.ToArray(),
            allowedParameters);
        return error == null ? null : TypedResults.BadRequest(error);
    }

    /// <summary>
    /// Determines output format from request parameters and headers.
    /// </summary>
    public static bool TryGetOutputFormat(
        string? formatParameter,
        HttpContext context,
        bool isFeatureContent,
        out string outputFormat,
        out IResult? error)
    {
        var parameterMap = isFeatureContent ? _featureFormatParameters : _metadataFormatParameters;
        var supportedMediaTypes = isFeatureContent ? _featureSupportedMediaTypes : _metadataSupportedMediaTypes;
        var defaultMediaType = isFeatureContent ? MediaTypes.GeoJson : MediaTypes.Json;

        return TryGetOutputFormat(
            formatParameter,
            context,
            parameterMap,
            supportedMediaTypes,
            defaultMediaType,
            out outputFormat,
            out error);
    }

    /// <summary>
    /// Determines output format from request parameters and Accept negotiation.
    /// </summary>
    public static bool TryGetOutputFormat(
        string? formatParameter,
        HttpContext context,
        IReadOnlyDictionary<string, string> formatParameterMap,
        IReadOnlyList<string> supportedMediaTypes,
        string defaultMediaType,
        out string outputFormat,
        out IResult? error)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(formatParameterMap);
        ArgumentNullException.ThrowIfNull(supportedMediaTypes);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultMediaType);

        outputFormat = defaultMediaType;
        error = null;

        if (!string.IsNullOrWhiteSpace(formatParameter))
        {
            var normalized = formatParameter.Trim();
            if (formatParameterMap.TryGetValue(normalized, out var mappedMediaType) &&
                supportedMediaTypes.Any(mediaType => string.Equals(mediaType, mappedMediaType, StringComparison.OrdinalIgnoreCase)))
            {
                outputFormat = mappedMediaType;
                return true;
            }

            error = TypedResults.BadRequest($"Unsupported format '{formatParameter}'");
            return false;
        }

        var acceptHeader = context.Request.Headers.Accept;
        if (StringValues.IsNullOrEmpty(acceptHeader))
        {
            return true;
        }

        var acceptRanges = ContentNegotiationHelpers.ParseAcceptHeader(acceptHeader);
        if (acceptRanges.IsDefaultOrEmpty)
        {
            error = Results.StatusCode(StatusCodes.Status406NotAcceptable);
            return false;
        }

        if (ContentNegotiationHelpers.TrySelectBestMediaType(supportedMediaTypes, acceptHeader, out var selected))
        {
            outputFormat = selected;
            return true;
        }

        error = Results.StatusCode(StatusCodes.Status406NotAcceptable);
        return false;
    }

    /// <summary>
    /// Creates a standardized error response for invalid format negotiation.
    /// </summary>
    public static IResult CreateFormatError(HttpContext context, IResult? formatError)
    {
        if (formatError is BadRequest<string> badRequest)
        {
            return StandardErrorHelpers.CreateBadRequest(context, badRequest.Value ?? "Invalid format.");
        }

        if (formatError is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue)
        {
            return StandardErrorHelpers.CreateNotAcceptable(context, "Requested format is not acceptable.");
        }

        return StandardErrorHelpers.CreateBadRequest(context, "Invalid format.");
    }

    /// <summary>
    /// Gets a query parameter value, optionally trimmed.
    /// </summary>
    public static string? GetQueryValue(HttpRequest request, string key, bool trim = true)
    {
        if (!request.Query.TryGetValue(key, out var values))
        {
            return null;
        }

        var value = values.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return trim ? value.Trim() : value;
    }

    /// <summary>
    /// Builds URL with format parameter.
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
    /// Builds format-specific links for content negotiation.
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
    /// Adds alternate format links to existing link collection.
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
    /// Formats metadata response with HTML option.
    /// </summary>
    public static IResult FormatMetadataResponse<T>(
        T payload,
        JsonTypeInfo<T> typeInfo,
        string outputFormat,
        string title,
        string? jsonContentType = null)
    {
        if (string.Equals(outputFormat, MediaTypes.Html, StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(payload, typeInfo);
            var html = BuildHtmlDocument(title, json);
            return Results.Text(html, MediaTypes.Html);
        }

        var contentType = string.IsNullOrWhiteSpace(jsonContentType)
            ? (string.Equals(outputFormat, MediaTypes.SchemaJson, StringComparison.OrdinalIgnoreCase)
                ? MediaTypes.SchemaJson
                : MediaTypes.Json)
            : jsonContentType;
        return Results.Json(payload, typeInfo, contentType: contentType);
    }

    private static string BuildHtmlDocument(string title, string json)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedJson = WebUtility.HtmlEncode(json);

        // Extract navigation links from the JSON for a browsable interface
        var navLinks = ExtractLinksForHtml(json);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <title>{encodedTitle} - OGC API</title>
    <style>
        * {{ box-sizing: border-box; }}
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 20px 40px; color: #1a1a1a; background: #fafafa; }}
        .header {{ border-bottom: 2px solid #0077b6; padding-bottom: 12px; margin-bottom: 20px; }}
        .header h1 {{ margin: 0; color: #0077b6; font-size: 1.5em; }}
        .header .badge {{ display: inline-block; background: #0077b6; color: #fff; padding: 2px 8px; border-radius: 3px; font-size: 0.75em; margin-left: 8px; vertical-align: middle; }}
        nav {{ background: #fff; border: 1px solid #e0e0e0; border-radius: 6px; padding: 12px 16px; margin-bottom: 20px; }}
        nav h2 {{ margin: 0 0 8px 0; font-size: 0.9em; color: #555; text-transform: uppercase; letter-spacing: 0.5px; }}
        nav ul {{ list-style: none; padding: 0; margin: 0; display: flex; flex-wrap: wrap; gap: 8px; }}
        nav li a {{ display: inline-block; padding: 4px 12px; background: #e8f4f8; border-radius: 4px; text-decoration: none; color: #0077b6; font-size: 0.9em; }}
        nav li a:hover {{ background: #0077b6; color: #fff; }}
        nav li a .rel {{ color: #888; font-size: 0.8em; }}
        .json-container {{ background: #fff; border: 1px solid #e0e0e0; border-radius: 6px; max-height: 75vh; overflow: auto; }}
        pre {{ margin: 0; padding: 16px; font-size: 0.85em; line-height: 1.5; white-space: pre-wrap; word-wrap: break-word; }}
    </style>
</head>
<body>
    <div class=""header"">
        <h1>{encodedTitle}<span class=""badge"">OGC API</span></h1>
    </div>
    {navLinks}
    <div class=""json-container"">
        <pre><code>{encodedJson}</code></pre>
    </div>
</body>
</html>";
    }

    private static string ExtractLinksForHtml(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("links", out var linksElement) ||
                linksElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("<nav><h2>Links</h2><ul>");

            foreach (var link in linksElement.EnumerateArray())
            {
                var href = link.TryGetProperty("href", out var hrefProp) ? hrefProp.GetString() : null;
                var rel = link.TryGetProperty("rel", out var relProp) ? relProp.GetString() : null;
                var linkTitle = link.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;

                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                // Only show HTML-navigable links
                var type = link.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                var displayTitle = WebUtility.HtmlEncode(linkTitle ?? rel ?? href);
                var relLabel = !string.IsNullOrEmpty(rel) ? $" <span class=\"rel\">[{WebUtility.HtmlEncode(rel)}]</span>" : "";
                var htmlHref = type != null && !type.Contains("html", StringComparison.OrdinalIgnoreCase)
                    ? href + (href.Contains('?') ? "&" : "?") + "f=html"
                    : href;

                sb.Append($"<li><a href=\"{WebUtility.HtmlEncode(htmlHref)}\">{displayTitle}{relLabel}</a></li>");
            }

            sb.Append("</ul></nav>");
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
