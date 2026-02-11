// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Ogc.Common;

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

        var acceptHeader = context.Request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        var acceptRanges = ParseAcceptHeader(acceptHeader);
        if (acceptRanges.IsDefaultOrEmpty)
        {
            return true;
        }

        if (TrySelectBestMediaType(supportedMediaTypes, acceptRanges, out var selected))
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

    private static bool TrySelectBestMediaType(
        IReadOnlyList<string> supportedMediaTypes,
        ImmutableArray<AcceptMediaRange> acceptRanges,
        out string selectedMediaType)
    {
        selectedMediaType = string.Empty;
        var best = new AcceptSelection();
        var hasMatch = false;

        for (var i = 0; i < supportedMediaTypes.Count; i++)
        {
            var candidate = supportedMediaTypes[i];
            if (!TryGetBestRangeMatch(candidate, acceptRanges, out var candidateSelection))
            {
                continue;
            }

            candidateSelection = candidateSelection with { SupportedOrder = i };
            if (!hasMatch || candidateSelection.IsPreferredTo(best))
            {
                best = candidateSelection;
                selectedMediaType = candidate;
                hasMatch = true;
            }
        }

        return hasMatch;
    }

    private static bool TryGetBestRangeMatch(
        string candidateMediaType,
        ImmutableArray<AcceptMediaRange> acceptRanges,
        out AcceptSelection bestSelection)
    {
        bestSelection = default;
        var hasMatch = false;

        for (var i = 0; i < acceptRanges.Length; i++)
        {
            var range = acceptRanges[i];
            if (!TryMatchMediaRange(range, candidateMediaType, out var specificity))
            {
                continue;
            }

            var selection = new AcceptSelection(range.Quality, specificity, range.Order, int.MaxValue);
            if (!hasMatch || selection.IsPreferredTo(bestSelection))
            {
                bestSelection = selection;
                hasMatch = true;
            }
        }

        return hasMatch;
    }

    private static bool TryMatchMediaRange(
        AcceptMediaRange range,
        string candidateMediaType,
        out int specificity)
    {
        specificity = -1;

        if (!TryParseMediaType(candidateMediaType, out var candidateType, out var candidateSubtype))
        {
            return false;
        }

        // Compatibility: clients often request generic XML for GML representations.
        if (string.Equals(range.Type, "text", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(range.Subtype, "xml", StringComparison.OrdinalIgnoreCase) &&
            candidateSubtype.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            specificity = 0;
            return true;
        }

        var typeMatches = range.Type == "*" || string.Equals(range.Type, candidateType, StringComparison.OrdinalIgnoreCase);
        if (!typeMatches)
        {
            return false;
        }

        if (range.Subtype == "*")
        {
            specificity = range.Type == "*" ? 0 : 1;
            return true;
        }

        if (range.Subtype.StartsWith("*+", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = range.Subtype[1..];
            if (candidateSubtype.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                specificity = 1;
                return true;
            }

            return false;
        }

        if (string.Equals(range.Subtype, candidateSubtype, StringComparison.OrdinalIgnoreCase))
        {
            specificity = 2;
            return true;
        }

        if (string.Equals(range.Subtype, "xml", StringComparison.OrdinalIgnoreCase) &&
            candidateSubtype.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            specificity = 1;
            return true;
        }

        return false;
    }

    private static ImmutableArray<AcceptMediaRange> ParseAcceptHeader(string acceptHeader)
    {
        var values = acceptHeader.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var builder = ImmutableArray.CreateBuilder<AcceptMediaRange>(values.Length);
        var order = 0;

        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var segments = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            if (!TryParseMediaType(segments[0], out var type, out var subtype))
            {
                continue;
            }

            var quality = 1.0;
            for (var i = 1; i < segments.Length; i++)
            {
                var parameter = segments[i].Trim();
                if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var qValue = parameter[2..].Trim();
                if (double.TryParse(qValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    quality = Math.Clamp(parsed, 0.0, 1.0);
                }

                break;
            }

            if (quality <= 0.0)
            {
                order++;
                continue;
            }

            builder.Add(new AcceptMediaRange(type, subtype, quality, order));
            order++;
        }

        return builder.ToImmutable();
    }

    private static bool TryParseMediaType(string mediaType, out string type, out string subtype)
    {
        type = string.Empty;
        subtype = string.Empty;

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        var withoutParameters = mediaType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        var separator = withoutParameters.IndexOf('/');
        if (separator <= 0 || separator >= withoutParameters.Length - 1)
        {
            return false;
        }

        type = withoutParameters[..separator].Trim();
        subtype = withoutParameters[(separator + 1)..].Trim();

        return !(string.IsNullOrEmpty(type) || string.IsNullOrEmpty(subtype));
    }

    private readonly record struct AcceptMediaRange(string Type, string Subtype, double Quality, int Order);

    private readonly record struct AcceptSelection(double Quality, int Specificity, int AcceptOrder, int SupportedOrder)
    {
        public bool IsPreferredTo(AcceptSelection other)
        {
            if (Quality != other.Quality)
            {
                return Quality > other.Quality;
            }

            if (Specificity != other.Specificity)
            {
                return Specificity > other.Specificity;
            }

            if (AcceptOrder != other.AcceptOrder)
            {
                return AcceptOrder < other.AcceptOrder;
            }

            return SupportedOrder < other.SupportedOrder;
        }
    }

    private static string BuildHtmlDocument(string title, string json)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedJson = WebUtility.HtmlEncode(json);
        return $@"<!DOCTYPE html>
<html>
<head>
    <title>{encodedTitle}</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; }}
        pre {{ background: #f5f5f5; padding: 20px; border-radius: 5px; overflow: auto; }}
        .title {{ color: #333; margin-bottom: 20px; }}
    </style>
</head>
<body>
    <h1 class=""title"">{encodedTitle}</h1>
    <pre><code>{encodedJson}</code></pre>
</body>
</html>";
    }
}
