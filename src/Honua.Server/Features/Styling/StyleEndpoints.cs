// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Domain;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Styling;

/// <summary>
/// Public read endpoint for the canonical MapLibre style of a layer.
/// </summary>
/// <remarks>
/// <para>
/// The route <c>GET /api/styles/{layerId}.json</c> is a <b>deprecated</b>
/// layerId-keyed alias. The canonical style identifier is <c>styleId</c> via
/// <c>GET /ogc/styles/{styleId}</c> (ADR-0048), which serves MapLibre and
/// derived SLD encodings through content negotiation.
/// </para>
/// <para>
/// The alias is kept working for back-compat and emits advisory
/// <c>Deprecation</c>/<c>Sunset</c> response headers. Per ADR-0048 it is
/// retired only after the cross-repo styleId rollout (Phase 3) completes.
/// </para>
/// </remarks>
internal static class StyleEndpoints
{
    /// <summary>
    /// Query parameter name for theme selection.  Bound when present on style GETs and
    /// used by the cache policy as a vary-by-query key so themed responses do not
    /// collide with the canonical style cache entry.
    /// </summary>
    public const string ThemeQueryParameter = "theme";

    public static IEndpointRouteBuilder MapStyleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/styles/{layerId:int}.json", HandleGetStyle)
            .WithDisplayName("Get MapLibre Style (deprecated layerId alias)")
            .WithName("GetMapLibreStyle")
            .WithSummary("[Deprecated] Get MapLibre style for a layer by layerId")
            .WithDescription("DEPRECATED layerId-keyed alias. The canonical style identifier is styleId via GET /ogc/styles/{styleId} (ADR-0048); content negotiation there serves MapLibre and derived SLD. This endpoint keeps working as a back-compat alias and returns advisory Deprecation/Sunset response headers; it will be removed only after downstream styleId migration completes. Returns a MapLibre style JSON document for the requested layer. Pass ?theme=dark|colorblind-safe|print for theme transforms.")
            .WithTags("Styles")
            .CacheOutput("LayerStyle")
            .WithETag()
            .Produces<JsonElement>(StatusCodes.Status200OK, MediaTypes.Json)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> HandleGetStyle(
        int layerId,
        HttpContext context,
        [FromQuery(Name = ThemeQueryParameter)] string? theme,
        [FromServices] ILayerStyleService styleService,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Advisory deprecation signaling for the layerId-keyed alias. Behavior is
        // unchanged; the canonical identifier is styleId via /ogc/styles/{styleId}.
        StyleDeprecationHeaders.Apply(context, "/ogc/styles");

        if (!TryParseTheme(theme, out var themeProfile))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported theme '{theme}'. Use 'default', 'dark', 'colorblind-safe', or 'print'.");
        }

        if (layerId < 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Layer id '{layerId}' is invalid.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid || layerValidation.Resource is null)
        {
            return layerValidation.ErrorResult!;
        }

        var snapshot = await styleService.GetStyleAsync(layerValidation.Resource, layerId, cancellationToken);
        var styleElement = snapshot?.MapLibreStyle;

        if (!styleElement.HasValue || styleElement.Value.ValueKind == JsonValueKind.Null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Style for layer {layerId} not found.");
        }

        if (themeProfile == ThemeProfile.Default)
        {
            return Results.Json(styleElement.Value, StyleJsonContext.Default.JsonElement, contentType: MediaTypes.Json);
        }

        var rawJson = styleElement.Value.GetRawText();
        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Styling.StyleEndpoints");
        var themed = StyleThemeTransformer.ApplyTheme(rawJson, themeProfile, logger, layerId);
        var themeName = themeProfile switch
        {
            ThemeProfile.Dark => "Dark",
            ThemeProfile.ColorblindSafe => "ColorblindSafe",
            ThemeProfile.Print => "Print",
            _ => "Default"
        };
        LayerStyleLog.ThemeApplied(logger, themeName, layerId);

        var themedElement = StyleJsonUtilities.ParseJsonElement(themed);
        if (!themedElement.HasValue)
        {
            return Results.Json(styleElement.Value, StyleJsonContext.Default.JsonElement, contentType: MediaTypes.Json);
        }

        return Results.Json(themedElement.Value, StyleJsonContext.Default.JsonElement, contentType: MediaTypes.Json);
    }

    internal static bool TryParseTheme(string? raw, out ThemeProfile theme)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            theme = ThemeProfile.Default;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "default":
                theme = ThemeProfile.Default;
                return true;
            case "dark":
                theme = ThemeProfile.Dark;
                return true;
            case "colorblind-safe":
            case "colorblindsafe":
                theme = ThemeProfile.ColorblindSafe;
                return true;
            case "print":
                theme = ThemeProfile.Print;
                return true;
            default:
                theme = ThemeProfile.Default;
                return false;
        }
    }
}
