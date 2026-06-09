// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text;
using Honua.Core.Configuration;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.Ogc.Api.Styles.Handlers;
using Honua.Protocols.Ogc.Api.Styles.Models;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Ogc.Api.Styles;

/// <summary>
/// OGC API - Styles endpoints (ADR-0048, Phase 1). Projects Honua's per-layer style
/// storage as conformant OGC styles: landing, conformance, OpenAPI, the styles list,
/// content-negotiated stylesheets (MapLibre canonical + derived SLD 1.0/1.1), style
/// metadata, and a partial <c>manage-styles</c> surface (PUT only). Standalone style
/// create (POST) and delete (DELETE) return 501 until the Phase 2 independent style
/// catalog lands (issue #1389).
/// </summary>
public static class OgcStylesEndpoints
{
    private static readonly ImmutableHashSet<string> MetadataQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f");

    private static readonly ImmutableHashSet<string> OpenApiQueryParameters =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "f");

    /// <summary>
    /// Maps OGC API - Styles endpoints to the application.
    /// </summary>
    public static void MapOgcStylesEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/ogc/styles")
            .WithTags("OGC API - Styles");

        group.MapGet(string.Empty, GetStyles)
            .WithDisplayName("Styles List")
            .WithName("GetStylesList")
            .WithSummary("Get the OGC API - Styles landing/styles list")
            .WithDescription("Returns the landing-page links and the list of styles available on the server")
            .Produces<StylesList>(StatusCodes.Status200OK, MediaTypes.Json)
            .CacheOutput("OgcStylesList");

        group.MapGet("/conformance", GetConformance)
            .WithDisplayName("Styles API Conformance")
            .WithName("GetStylesConformance")
            .WithSummary("Get OGC API - Styles conformance classes")
            .WithDescription("Returns the conformance classes that the server implements from OGC API - Styles")
            .Produces<OgcStylesConformance>()
            .CacheOutput("OgcStylesConformance");

        group.MapGet("/openapi.json", GetOpenApiSpec)
            .WithDisplayName("Styles API OpenAPI Specification")
            .WithName("GetStylesOpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API - Styles")
            .WithDescription("The OpenAPI specification describes all available OGC API - Styles endpoints")
            .Produces<object>(StatusCodes.Status200OK, MediaTypes.OpenApi)
            .Produces(StatusCodes.Status404NotFound)
            .CacheOutput("OgcStylesOpenApi");

        group.MapGet("/{styleId}", GetStylesheet)
            .WithDisplayName("Get Stylesheet")
            .WithName("GetStylesheet")
            .WithSummary("Get a stylesheet")
            .WithDescription("Returns the stylesheet for a style. The encoding is selected by the Accept header: MapLibre/Mapbox JSON (default), derived SLD 1.0/1.1, or derived Esri drawingInfo.")
            .Produces<object>(StatusCodes.Status200OK, MediaTypes.MapboxStyle)
            .Produces(StatusCodes.Status200OK, contentType: MediaTypes.Sld10)
            .Produces(StatusCodes.Status200OK, contentType: MediaTypes.Sld11)
            .Produces(StatusCodes.Status200OK, contentType: MediaTypes.EsriDrawingInfo)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status406NotAcceptable)
            .CacheOutput("OgcStylesStylesheet");

        group.MapGet("/{styleId}/metadata", GetStyleMetadata)
            .WithDisplayName("Get Style Metadata")
            .WithName("GetStyleMetadata")
            .WithSummary("Get style metadata")
            .WithDescription("Returns descriptive metadata for a style, including stylesheet, schema (describedby), and preview links")
            .Produces<StyleMetadataResponse>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces(StatusCodes.Status404NotFound)
            .CacheOutput("OgcStylesMetadata");

        group.MapPut("/{styleId}", UpdateStyle)
            .WithDisplayName("Update Style")
            .WithName("UpdateStyle")
            .WithSummary("Update an existing style (manage-styles)")
            .WithDescription("Validates and stores a MapLibre stylesheet for an existing style. Honors Prefer: handling=strict and ?validate. Creating standalone styles is not yet supported (Phase 2).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .RequireAdminAuthorization();

        group.MapPost(string.Empty, CreateStyle)
            .WithDisplayName("Create Style")
            .WithName("CreateStyle")
            .WithSummary("Create a standalone style (manage-styles)")
            .WithDescription("Validates and stores a MapLibre stylesheet as a new standalone style in the independent style catalog (ADR-0048 Phase 2). The new style's stable identifier is returned in the Location header. Honors Prefer: handling=strict and ?validate.")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .RequireAdminAuthorization();

        group.MapDelete("/{styleId}", DeleteStyle)
            .WithDisplayName("Delete Style")
            .WithName("DeleteStyle")
            .WithSummary("Delete a style (manage-styles)")
            .WithDescription("Deletes a standalone style from the independent style catalog (ADR-0048 Phase 2).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminAuthorization();
    }

    /// <summary>
    /// Get the OGC API - Styles styles list (also the landing surface).
    /// </summary>
    private static async Task<IResult> GetStyles(
        HttpContext context,
        string? f,
        IOgcStyleProjection projection,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                MetadataQueryParameters,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/styles";

        var summaries = await projection.ListStylesAsync(cancellationToken).ConfigureAwait(false);
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Ogc.Api.Styles.OgcStylesEndpoints");
        OgcStylesLog.StylesListed(logger, summaries.Count);

        var entries = ImmutableArray.CreateBuilder<StyleEntry>(summaries.Count);
        foreach (var summary in summaries)
        {
            entries.Add(new StyleEntry
            {
                Id = summary.StyleId,
                Title = summary.Title,
                Links = BuildStylesheetLinks(baseUrl, summary.StyleId)
            });
        }

        var listLinks = OgcCoreMetadataUtilities.BuildLandingPageLinks(context, basePath, outputFormat);
        listLinks.Add(Link.Create(
            href: $"{baseUrl}/ogc/styles/conformance",
            rel: RelationTypes.Conformance,
            type: MediaTypes.Json,
            title: "Conformance declaration"));
        listLinks.Add(Link.Create(
            href: $"{baseUrl}/ogc/styles/openapi.json",
            rel: RelationTypes.ServiceDesc,
            type: MediaTypes.OpenApi,
            title: "API definition"));

        var stylesList = new StylesList
        {
            Styles = entries.ToImmutable(),
            Links = listLinks.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            stylesList,
            OgcStylesJsonContext.Default.StylesList,
            outputFormat,
            "Styles");
    }

    /// <summary>
    /// Get OGC API - Styles conformance classes.
    /// </summary>
    private static async Task<IResult> GetConformance(
        OgcStylesConformanceHandler handler,
        CancellationToken cancellationToken = default)
    {
        var result = await handler.GetConformanceAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetOpenApiSpec(
        HttpContext context,
        string? f,
        IWebHostEnvironment environment)
    {
        const string fallbackSpec = """
        {
          "openapi": "3.0.3",
          "info": {
            "title": "Honua OGC API Styles",
            "description": "OGC API Styles adapter over Honua per-layer style storage (ADR-0048, Phase 1)",
            "version": "1.0.0"
          },
          "paths": {}
        }
        """;

        return await OgcCoreMetadataUtilities.GetOpenApiSpecAsync(
            context,
            f,
            environment,
            OpenApiQueryParameters,
            "ogc-styles-openapi.json",
            fallbackSpec).ConfigureAwait(false);
    }

    /// <summary>
    /// Get a stylesheet, content-negotiated by the Accept header.
    /// </summary>
    private static async Task<IResult> GetStylesheet(
        string styleId,
        HttpContext context,
        IOgcStyleProjection projection,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        if (!TryNegotiateEncoding(context, out var encoding))
        {
            return StandardErrorHelpers.CreateNotAcceptable(
                context,
                "Supported stylesheet media types are application/vnd.mapbox.style+json, application/vnd.ogc.sld+xml;version=1.0, and application/vnd.ogc.sld+xml;version=1.1.");
        }

        var stylesheet = await projection.GetStylesheetAsync(styleId, encoding, cancellationToken).ConfigureAwait(false);
        if (stylesheet is null)
        {
            var logger = loggerFactory.CreateLogger("Honua.Protocols.Ogc.Api.Styles.OgcStylesEndpoints");
            OgcStylesLog.StyleNotFound(logger, styleId);
            return StandardErrorHelpers.CreateNotFound(context, $"Style '{styleId}' not found.");
        }

        return Results.Content(stylesheet.Content, stylesheet.MediaType, Encoding.UTF8);
    }

    /// <summary>
    /// Get style metadata.
    /// </summary>
    private static async Task<IResult> GetStyleMetadata(
        string styleId,
        HttpContext context,
        string? f,
        IOgcStyleProjection projection,
        CancellationToken cancellationToken = default)
    {
        if (!OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
                context,
                f,
                MetadataQueryParameters,
                out var outputFormat,
                out var errorResult))
        {
            return errorResult!;
        }

        var metadata = await projection.GetStyleMetadataAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Style '{styleId}' not found.");
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var links = BuildStylesheetLinks(baseUrl, styleId).ToBuilder();
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/styles/{Uri.EscapeDataString(styleId)}/metadata",
            rel: RelationTypes.Self,
            type: MediaTypes.Json,
            title: "Style metadata"));
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/features/collections/{Uri.EscapeDataString(styleId)}/queryables",
            rel: RelationTypes.DescribedBy,
            type: MediaTypes.SchemaJson,
            title: "Schema"));
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/maps/collections/{Uri.EscapeDataString(styleId)}/map",
            rel: RelationTypes.Preview,
            type: MediaTypes.Png,
            title: "Preview"));

        var response = new StyleMetadataResponse
        {
            Id = metadata.StyleId,
            Title = metadata.Title,
            Description = metadata.Description,
            Keywords = metadata.Keywords.Count > 0 ? metadata.Keywords.ToImmutableArray() : null,
            License = metadata.License,
            Version = metadata.Version,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            response,
            OgcStylesJsonContext.Default.StyleMetadataResponse,
            outputFormat,
            "Style metadata");
    }

    /// <summary>
    /// Update an existing style (manage-styles, PUT). Honors Prefer: handling=strict
    /// and ?validate for strict validation.
    /// </summary>
    private static async Task<IResult> UpdateStyle(
        string styleId,
        HttpContext context,
        IOgcStyleProjection projection,
        IOptions<LimitsOptions> limitsOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var contentType = context.Request.ContentType;
        var isEsri = IsEsriDrawingInfoContentType(contentType);
        if (!string.IsNullOrWhiteSpace(contentType)
            && !IsMapboxStyleContentType(contentType)
            && !isEsri)
        {
            return StandardErrorHelpers.CreateUnsupportedMediaType(
                context,
                "manage-styles accepts MapLibre/Mapbox style JSON (application/vnd.mapbox.style+json or application/json) or Esri drawingInfo (application/vnd.esri.drawinginfo+json).");
        }

        var bodyRead = await RequestBodySizeGuard.ReadUtf8TextAsync(
            context,
            RequestBodySizeGuard.ResolveMaxBodyBytes(limitsOptions),
            cancellationToken).ConfigureAwait(false);
        if (bodyRead.TooLarge)
        {
            return bodyRead.ErrorResult!;
        }

        var body = bodyRead.Body;

        if (string.IsNullOrWhiteSpace(body))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                isEsri
                    ? "Request body must contain an Esri drawingInfo document."
                    : "Request body must contain a MapLibre style document.");
        }

        var strict = IsStrictRequested(context);

        var result = isEsri
            ? await projection.UpdateStyleFromDrawingInfoAsync(styleId, body, strict, cancellationToken).ConfigureAwait(false)
            : await projection.UpdateStyleAsync(styleId, body, strict, cancellationToken).ConfigureAwait(false);
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Ogc.Api.Styles.OgcStylesEndpoints");

        switch (result.Status)
        {
            case OgcStyleUpdateStatus.Updated:
                OgcStylesLog.StyleUpdated(logger, styleId);
                // Surface lossy-conversion warnings non-blockingly so the client can show them (the canonical
                // MapLibre was still stored). Mirrors the unsupportedSymbolizers[] contract.
                if (result.UnsupportedSymbolizers is { Count: > 0 })
                {
                    context.Response.Headers["X-Style-Unsupported-Symbolizers"] =
                        string.Join(" | ", result.UnsupportedSymbolizers);
                }

                return Results.NoContent();
            case OgcStyleUpdateStatus.NotFound:
                OgcStylesLog.StyleNotFound(logger, styleId);
                return StandardErrorHelpers.CreateNotFound(context, $"Style '{styleId}' not found.");
            default:
                OgcStylesLog.StyleUpdateRejected(logger, styleId, result.ErrorMessage ?? "invalid");
                var detail = result.ErrorMessage ?? "Style is invalid.";
                if (result.UnsupportedSymbolizers is { Count: > 0 })
                {
                    detail = $"{detail} Unsupported: {string.Join(" | ", result.UnsupportedSymbolizers)}";
                }

                return StandardErrorHelpers.CreateBadRequest(context, detail);
        }
    }

    /// <summary>
    /// Create a standalone style (manage-styles, POST). Stores a MapLibre stylesheet in
    /// the independent style catalog (ADR-0048 Phase 2). The optional desired identifier
    /// is read from the X-Style-Id header; otherwise the server assigns one.
    /// </summary>
    private static async Task<IResult> CreateStyle(
        HttpContext context,
        IOgcStyleProjection projection,
        IOptions<LimitsOptions> limitsOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var contentType = context.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType)
            && !IsMapboxStyleContentType(contentType))
        {
            return StandardErrorHelpers.CreateUnsupportedMediaType(
                context,
                "manage-styles create accepts only MapLibre/Mapbox style JSON (application/vnd.mapbox.style+json or application/json).");
        }

        var bodyRead = await RequestBodySizeGuard.ReadUtf8TextAsync(
            context,
            RequestBodySizeGuard.ResolveMaxBodyBytes(limitsOptions),
            cancellationToken).ConfigureAwait(false);
        if (bodyRead.TooLarge)
        {
            return bodyRead.ErrorResult!;
        }

        var body = bodyRead.Body;

        if (string.IsNullOrWhiteSpace(body))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Request body must contain a MapLibre style document.");
        }

        string? requestedStyleId = null;
        if (context.Request.Headers.TryGetValue("X-Style-Id", out var styleIdHeader))
        {
            requestedStyleId = styleIdHeader.ToString();
        }

        var strict = IsStrictRequested(context);
        var result = await projection.CreateStyleAsync(requestedStyleId, body, strict, cancellationToken).ConfigureAwait(false);
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Ogc.Api.Styles.OgcStylesEndpoints");

        switch (result.Status)
        {
            case OgcStyleCreateStatus.Created:
                var styleId = result.StyleId!;
                OgcStylesLog.StyleCreated(logger, styleId);
                var baseUrl = BaseUrlResolver.GetBaseUrl(context);
                var location = $"{baseUrl}/ogc/styles/{Uri.EscapeDataString(styleId)}";
                context.Response.Headers.Location = location;
                var entry = new StyleEntry
                {
                    Id = styleId,
                    Links = BuildStylesheetLinks(baseUrl, styleId)
                };
                return Results.Json(
                    entry,
                    OgcStylesJsonContext.Default.StyleEntry,
                    contentType: MediaTypes.Json,
                    statusCode: StatusCodes.Status201Created);
            case OgcStyleCreateStatus.Conflict:
                OgcStylesLog.StyleCreateRejected(logger, result.ErrorMessage ?? "conflict");
                return StandardErrorHelpers.CreateConflict(context, result.ErrorMessage ?? "A style with that identifier already exists.");
            default:
                OgcStylesLog.StyleCreateRejected(logger, result.ErrorMessage ?? "invalid");
                return StandardErrorHelpers.CreateBadRequest(context, result.ErrorMessage ?? "MapLibre style is invalid.");
        }
    }

    /// <summary>
    /// Delete a standalone style (manage-styles, DELETE) from the independent style
    /// catalog (ADR-0048 Phase 2).
    /// </summary>
    private static async Task<IResult> DeleteStyle(
        string styleId,
        HttpContext context,
        IOgcStyleProjection projection,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var result = await projection.DeleteStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        var logger = loggerFactory.CreateLogger("Honua.Protocols.Ogc.Api.Styles.OgcStylesEndpoints");

        switch (result.Status)
        {
            case OgcStyleDeleteStatus.Deleted:
                OgcStylesLog.StyleDeleted(logger, styleId);
                return Results.NoContent();
            case OgcStyleDeleteStatus.Forbidden:
                return StandardErrorHelpers.CreateBadRequest(context, result.ErrorMessage ?? $"Style '{styleId}' cannot be deleted through this surface.");
            default:
                OgcStylesLog.StyleNotFound(logger, styleId);
                return StandardErrorHelpers.CreateNotFound(context, result.ErrorMessage ?? $"Style '{styleId}' not found.");
        }
    }

    private static ImmutableArray<Link> BuildStylesheetLinks(string baseUrl, string styleId)
    {
        var encoded = Uri.EscapeDataString(styleId);
        var stylesheetHref = $"{baseUrl}/ogc/styles/{encoded}";
        var links = ImmutableArray.CreateBuilder<Link>(4);
        links.Add(Link.Create(
            href: stylesheetHref,
            rel: RelationTypes.Stylesheet,
            type: MediaTypes.MapboxStyle,
            title: "MapLibre/Mapbox stylesheet"));
        links.Add(Link.Create(
            href: stylesheetHref,
            rel: RelationTypes.Stylesheet,
            type: MediaTypes.Sld10,
            title: "SLD 1.0 stylesheet (derived)"));
        links.Add(Link.Create(
            href: stylesheetHref,
            rel: RelationTypes.Stylesheet,
            type: MediaTypes.Sld11,
            title: "SLD 1.1 stylesheet (derived)"));
        links.Add(Link.Create(
            href: $"{baseUrl}/ogc/styles/{encoded}/metadata",
            rel: RelationTypes.DescribedBy,
            type: MediaTypes.Json,
            title: "Style metadata"));
        return links.ToImmutable();
    }

    /// <summary>
    /// Negotiates the stylesheet encoding from the Accept header. MapLibre/Mapbox JSON
    /// is the default when no specific stylesheet media type is requested.
    /// </summary>
    private static bool TryNegotiateEncoding(HttpContext context, out OgcStyleEncoding encoding)
    {
        encoding = OgcStyleEncoding.MapboxStyle;

        var accept = context.Request.Headers.Accept;
        if (accept.Count == 0)
        {
            return true;
        }

        var matchedAny = false;
        var bestQuality = -1d;
        var wildcard = false;

        foreach (var headerValue in accept)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                continue;
            }

            foreach (var token in headerValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!MediaTypeHeaderValue.TryParse(token, out var media))
                {
                    continue;
                }

                var quality = media.Quality ?? 1d;
                var mediaType = media.MediaType.Value ?? string.Empty;

                if (mediaType is "*/*" or "application/*")
                {
                    wildcard = true;
                    continue;
                }

                if (TryMapMediaType(mediaType, media, out var candidate) && quality > bestQuality)
                {
                    bestQuality = quality;
                    encoding = candidate;
                    matchedAny = true;
                }
            }
        }

        if (matchedAny)
        {
            return true;
        }

        // No explicit stylesheet media type matched: fall back to MapLibre default only
        // when the client accepts anything; otherwise the request is not acceptable.
        encoding = OgcStyleEncoding.MapboxStyle;
        return wildcard;
    }

    private static bool TryMapMediaType(string mediaType, MediaTypeHeaderValue media, out OgcStyleEncoding encoding)
    {
        encoding = OgcStyleEncoding.MapboxStyle;
        switch (mediaType)
        {
            case "application/vnd.mapbox.style+json":
            case "application/json":
                encoding = OgcStyleEncoding.MapboxStyle;
                return true;
            case "application/vnd.ogc.sld+xml":
                encoding = ReadSldVersion(media) == "1.1"
                    ? OgcStyleEncoding.Sld11
                    : OgcStyleEncoding.Sld10;
                return true;
            case "application/vnd.esri.drawinginfo+json":
                encoding = OgcStyleEncoding.EsriDrawingInfo;
                return true;
            default:
                return false;
        }
    }

    private static string? ReadSldVersion(MediaTypeHeaderValue media)
    {
        foreach (var parameter in media.Parameters)
        {
            if (string.Equals(parameter.Name.Value, "version", StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value.Value;
            }
        }

        return null;
    }

    private static bool IsMapboxStyleContentType(string contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media))
        {
            return false;
        }

        var mediaType = media.MediaType.Value ?? string.Empty;
        return mediaType is "application/vnd.mapbox.style+json" or "application/json";
    }

    private static bool IsEsriDrawingInfoContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || !MediaTypeHeaderValue.TryParse(contentType, out var media))
        {
            return false;
        }

        return string.Equals(media.MediaType.Value, "application/vnd.esri.drawinginfo+json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictRequested(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("validate", out var validateValues))
        {
            foreach (var value in validateValues)
            {
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "strict", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (var prefer in context.Request.Headers["Prefer"])
        {
            if (prefer is null)
            {
                continue;
            }

            foreach (var token in prefer.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Equals("handling=strict", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
