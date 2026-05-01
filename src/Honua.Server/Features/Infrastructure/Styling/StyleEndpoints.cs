// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Styling;

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
            .WithDisplayName("Get MapLibre Style")
            .WithName("GetMapLibreStyle")
            .WithSummary("Get MapLibre style for a layer")
            .WithDescription("Returns a MapLibre style JSON document for the requested layer. Pass ?theme=dark|colorblind-safe|print for theme transforms.")
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
        if (!TryParseTheme(theme, out var themeProfile))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Unsupported theme '{theme}'. Use 'default', 'dark', 'colorblind-safe', or 'print'.");
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
            context,
            layerId,
            cancellationToken: cancellationToken);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var layer = layerValidation.Layer!;

        var snapshot = await styleService.GetStyleAsync(layer, cancellationToken);
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
        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Infrastructure.Styling.StyleEndpoints");
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
