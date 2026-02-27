// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private const string InvalidRendererJsonMessage = "classificationDef must be valid JSON.";

    private static async Task<IResult> HandleGenerateRenderer(
        HttpContext context)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.GenerateRenderer, out var error))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [error ?? "Invalid query parameter."]);
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        var layerError = RouteValidationHelpers.ValidateLayerId(context, out var layerId);
        if (layerError is not null)
        {
            return layerError;
        }

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var resourceResult = await resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
        if (!resourceResult.IsValid)
        {
            var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";
            if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
            {
                return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
            }

            return StandardErrorHelpers.CreateNotFound(context, errorMessage);
        }

        var service = resourceResult.Resource!.Service;
        var layer = resourceResult.Resource.Layer;
        var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
        if (accessError != null)
        {
            return accessError;
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        var classificationDef = GetValueString(values, "classificationDef");
        if (!string.IsNullOrWhiteSpace(classificationDef)
            && !TryParseJsonPayload(classificationDef, out var jsonError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid classificationDef",
                [jsonError ?? InvalidRendererJsonMessage]);
        }

        if (!layer.HasGeometry)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer does not support renderers");
        }

        // When a classificationDef is provided, return an error rather than
        // silently ignoring it and returning a simple renderer.
        if (!string.IsNullOrWhiteSpace(classificationDef))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Classification renderers are not supported",
                ["classBreaksDef and uniqueValueDef classification types are not yet implemented. Omit classificationDef to generate a simple renderer."]);
        }

        var symbol = BuildSimpleSymbol(layer.GeometryType);
        if (symbol == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer geometry type is not supported");
        }

        var renderer = new Dictionary<string, object?>
        {
            ["type"] = "simple",
            ["symbol"] = symbol
        };

        return Results.Json(renderer, FeatureServerJsonContext.Default.DictionaryStringObject, contentType: "application/json");
    }

    private static Dictionary<string, object?>? BuildSimpleSymbol(GeometryType geometryType)
    {
        var strokeColor = new[] { 45, 105, 165, 255 };
        var fillColor = new[] { 45, 105, 165, 64 };
        var outline = new Dictionary<string, object?>
        {
            ["type"] = "esriSLS",
            ["style"] = "esriSLSSolid",
            ["color"] = strokeColor,
            ["width"] = 1
        };

        return geometryType switch
        {
            GeometryType.Point or GeometryType.MultiPoint => new Dictionary<string, object?>
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["color"] = strokeColor,
                ["size"] = 6,
                ["outline"] = outline
            },
            GeometryType.LineString or GeometryType.MultiLineString => new Dictionary<string, object?>
            {
                ["type"] = "esriSLS",
                ["style"] = "esriSLSSolid",
                ["color"] = strokeColor,
                ["width"] = 2
            },
            GeometryType.Polygon or GeometryType.MultiPolygon => new Dictionary<string, object?>
            {
                ["type"] = "esriSFS",
                ["style"] = "esriSFSSolid",
                ["color"] = fillColor,
                ["outline"] = outline
            },
            _ => null
        };
    }

    private static bool TryParseJsonPayload(string payload, out string? error)
    {
        error = null;

        try
        {
            using var _ = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            error = InvalidRendererJsonMessage;
            return false;
        }
    }
}
