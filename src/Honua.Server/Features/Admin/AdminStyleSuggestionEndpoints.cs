// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Styling;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoint for auto-cartographic style suggestions.
/// </summary>
internal static class AdminStyleSuggestionEndpoints
{
    internal sealed class AdminStyleSuggestionEndpointsLog;

    /// <summary>
    /// Maps the style suggestion endpoint to the admin metadata API group.
    /// </summary>
    public static void MapAdminStyleSuggestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/metadata/layers")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata", "Styles")
            .RequireAdminAuthorization();

        _ = group.MapPost("/{layerId:int}/suggest-style", HandleSuggestStyle)
            .WithName("SuggestLayerStyle")
            .WithSummary("Generate an advisory style suggestion for a layer based on data analysis")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleSuggestStyle(
        int layerId,
        StyleSuggestionRequest? request,
        HttpContext context,
        [FromServices] Core.Features.Validation.Abstractions.IResourceValidator resourceValidator,
        [FromServices] IStyleSuggestionService styleSuggestionService,
        [FromServices] ILicenseStatusProvider licenseProvider,
        [FromServices] ILogger<AdminStyleSuggestionEndpointsLog> logger,
        CancellationToken cancellationToken)
    {
        AdminStyleSuggestionLog.StyleSuggestionRequested(logger, layerId);

        // Validate layer exists
        var layerResult = await resourceValidator.ValidateLayerAsync(layerId, cancellationToken);
        if (!layerResult.IsValid || layerResult.Resource == null)
        {
            var statusCode = layerResult.ErrorCode == Core.Features.Validation.Abstractions.ResourceValidationError.InvalidIdentifier
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status404NotFound;
            var message = layerResult.ErrorMessage ?? $"Layer {layerId} not found.";
            return ProblemDetailsHelpers.CreateAdminProblem(context, statusCode, message);
        }

        var layer = layerResult.Resource;
        var licenseStatus = licenseProvider.GetCurrentStatus();
        var isPro = licenseStatus.Edition >= HonuaEdition.Pro;

        try
        {
            if (!isPro)
            {
                // Community: enhanced geometry-only defaults
                var enhancedStyle = StyleSuggestionGenerator.GenerateEnhancedDefaults(layer);
                var enhancedDrawingInfo = StyleSuggestionGenerator.GenerateEnhancedDrawingInfo(layer);

                var enhancedMapLibreJson = StyleJsonUtilities.Serialize(enhancedStyle);
                var enhancedDrawingInfoJson = StyleJsonUtilities.Serialize(enhancedDrawingInfo);

                AdminStyleSuggestionLog.StyleSuggestionGeometryOnly(logger, layerId, "Community");

                var communityLegend = new LegendInfo
                {
                    Title = layer.Name,
                    Entries = [new LegendEntry { Label = layer.Name, Color = "#2D69A5" }]
                };

                var communityResponse = BuildResponse(
                    enhancedMapLibreJson,
                    enhancedDrawingInfoJson,
                    communityLegend,
                    null, null,
                    "Default",
                    ["Geometry type: " + layer.GeometryType + ".",
                     "Community edition: using enhanced geometry-aware defaults.",
                     "Upgrade to Pro for field-based classification and smart palette selection."],
                    "Community");

                var communityPayload = ApiResponse<StyleSuggestionResponse>.CreateSuccess(communityResponse);
                return Results.Json(communityPayload, StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);
            }

            // Pro: full profiling, classification, palette selection
            var options = MapRequestToOptions(request, licenseStatus.Edition);
            var suggestion = await styleSuggestionService.SuggestAsync(layer, options, cancellationToken)
                .ConfigureAwait(false);

            // Generate MapLibre and GeoServices JSON
            var mapLibreStyle = StyleSuggestionGenerator.GenerateMapLibreStyle(layer, suggestion);
            var drawingInfo = StyleSuggestionGenerator.GenerateDrawingInfo(layer, suggestion);

            var mapLibreJson = StyleJsonUtilities.Serialize(mapLibreStyle);
            var drawingInfoJson = StyleJsonUtilities.Serialize(drawingInfo);

            var fieldName = suggestion.SuggestedField?.Name;
            var method = suggestion.Classification?.Method.ToString();

            AdminStyleSuggestionLog.StyleSuggestionCompleted(
                logger, layerId, fieldName, method, suggestion.PaletteName);

            var response = BuildResponse(
                mapLibreJson,
                drawingInfoJson,
                suggestion.Legend,
                suggestion.SuggestedField,
                suggestion.Classification,
                suggestion.PaletteName,
                suggestion.Observations,
                licenseStatus.Edition.ToString());

            var payload = ApiResponse<StyleSuggestionResponse>.CreateSuccess(response);
            return Results.Json(payload, StyleSuggestionJsonContext.Default.ApiResponseStyleSuggestionResponse);
        }
        catch (Exception ex)
        {
            AdminStyleSuggestionLog.StyleSuggestionFailed(logger, layerId, ex.Message, ex);
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status500InternalServerError,
                "An internal error occurred while generating the style suggestion.");
        }
    }

    private static StyleSuggestionOptions MapRequestToOptions(StyleSuggestionRequest? request, HonuaEdition edition)
    {
        if (request == null)
            return new StyleSuggestionOptions { Edition = edition };

        ClassificationMethod? method = null;
        if (!string.IsNullOrEmpty(request.PreferredMethod) &&
            Enum.TryParse<ClassificationMethod>(request.PreferredMethod, ignoreCase: true, out var parsed))
        {
            method = parsed;
        }

        return new StyleSuggestionOptions
        {
            PreferredField = request.PreferredField,
            PreferredMethod = method,
            PreferredPalette = request.PreferredPalette,
            ClassCount = Math.Clamp(request.ClassCount ?? 5, 2, 12),
            Edition = edition
        };
    }

    private static StyleSuggestionResponse BuildResponse(
        string mapLibreJson,
        string drawingInfoJson,
        LegendInfo legend,
        FieldSuggestion? field,
        ClassificationResult? classification,
        string paletteName,
        IReadOnlyList<string> observations,
        string edition)
    {
        var legendEntries = legend.Entries.Select(e => new StyleSuggestionLegendEntry
        {
            Label = e.Label,
            Color = e.Color,
            MinValue = e.MinValue,
            MaxValue = e.MaxValue
        }).ToArray();

        return new StyleSuggestionResponse
        {
            MapLibreStyle = StyleJsonUtilities.ParseJsonElement(mapLibreJson),
            DrawingInfo = StyleJsonUtilities.ParseJsonElement(drawingInfoJson),
            Legend = new StyleSuggestionLegend
            {
                Title = legend.Title,
                Entries = legendEntries
            },
            SuggestedField = field != null ? new StyleSuggestionFieldInfo
            {
                Name = field.Name,
                Type = field.Type,
                Reason = field.Reason
            } : null,
            ClassificationMethod = classification?.Method.ToString(),
            PaletteName = paletteName,
            Observations = observations.ToArray(),
            Edition = edition
        };
    }
}
