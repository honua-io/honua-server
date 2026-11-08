// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Styling;

internal static class StyleEndpoints
{
    public const string ThemeQueryParameter = "theme";

    public static IEndpointRouteBuilder MapStyleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/styles/{publicationId}.json", HandleGetStyle)
            .WithDisplayName("Get MapLibre Style")
            .WithName("GetMapLibreStyle")
            .WithSummary("Get MapLibre style for a publication")
            .WithDescription("Returns a MapLibre style for the exact publication identifier. Pass ?theme=dark|colorblind-safe|print for theme transforms.")
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
        string publicationId,
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

        var layerValidation = await LayerValidationHelpers.ValidatePublicationWithAccessV2Async(
            context, publicationId, AuthorizationOperation.Query);
        if (!layerValidation.IsValid || layerValidation.Resource is null)
        {
            return layerValidation.ErrorResult!;
        }

        var storageLayerId = layerValidation.Snapshot!.ResolveStorageLayerId(layerValidation.Publication!);
        if (!storageLayerId.HasValue)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Style for publication '{publicationId}' not found.");
        }

        var snapshot = await styleService.GetStyleAsync(layerValidation.Resource, storageLayerId.Value, cancellationToken);
        var styleElement = snapshot?.MapLibreStyle;

        if (!styleElement.HasValue || styleElement.Value.ValueKind == JsonValueKind.Null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Style for publication '{publicationId}' not found.");
        }

        if (themeProfile == ThemeProfile.Default)
        {
            var resolvedStyle = StyleEndpointUrlResolver.Resolve(styleElement.Value, BaseUrlResolver.GetBaseUrl(context), context.Request);
            return Results.Json(resolvedStyle, StyleJsonContext.Default.JsonElement, contentType: MediaTypes.Json);
        }

        var rawJson = styleElement.Value.GetRawText();
        var logger = loggerFactory.CreateLogger("Honua.Server.Features.Styling.StyleEndpoints");
        var themed = StyleThemeTransformer.ApplyTheme(rawJson, themeProfile, logger, storageLayerId.Value);
        var themeName = themeProfile switch
        {
            ThemeProfile.Dark => "Dark",
            ThemeProfile.ColorblindSafe => "ColorblindSafe",
            ThemeProfile.Print => "Print",
            _ => "Default"
        };
        LayerStyleLog.ThemeApplied(logger, themeName, storageLayerId.Value);

        var themedElement = StyleJsonUtilities.ParseJsonElement(themed);
        if (!themedElement.HasValue)
        {
            var resolvedStyle = StyleEndpointUrlResolver.Resolve(styleElement.Value, BaseUrlResolver.GetBaseUrl(context), context.Request);
            return Results.Json(resolvedStyle, StyleJsonContext.Default.JsonElement, contentType: MediaTypes.Json);
        }

        var resolvedThemedStyle = StyleEndpointUrlResolver.Resolve(themedElement.Value, BaseUrlResolver.GetBaseUrl(context), context.Request);
        return Results.Json(resolvedThemedStyle, StyleJsonContext.Default.JsonElement, contentType: MediaTypes.Json);
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
