// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Validation;

namespace Honua.Protocols.GeoServices.FeatureServer;

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
        var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            resourceValidator,
            serviceId,
            layerId,
            context,
            logger: null,
            cancellationToken: cancellationToken);
        if (!validationResult.IsValid)
        {
            return validationResult.ErrorResult!;
        }

        var service = validationResult.Service!;
        var resource = validationResult.Resource!;
        var accessError = AccessPolicyHelpers.RequireResourceAccess(context, resource, service);
        if (accessError != null)
        {
            return accessError;
        }

        var values = ToCaseInsensitiveDictionary(context.Request.Query);
        var classificationDef = GetValueString(values, "classificationDef");
        JsonDocument? classificationDocument = null;
        if (!string.IsNullOrWhiteSpace(classificationDef)
            && !TryParseJsonDocument(classificationDef, out classificationDocument, out var jsonError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid classificationDef",
                [jsonError ?? InvalidRendererJsonMessage]);
        }

        using var _classificationDocument = classificationDocument;

        var geometryType = resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;
        // V2 canonical sources may carry geometry on Spatial OR via a
        // Geometry/Geography schema field (the typed Spatial section is
        // optional; pre-typed test fixtures still surface geometry only
        // through the schema). Treat either as "layer supports renderers".
        var hasGeometry = geometryType != MetadataV2GeometryType.None
            || resource.SchemaFields.Any(f => f.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography);
        if (!hasGeometry)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Layer does not support renderers");
        }

        // When a classificationDef is provided, generate the corresponding
        // classified renderer (classBreaksDef / uniqueValueDef) by reusing the
        // shared FeatureServer query/statistics pipeline.
        if (classificationDocument is not null)
        {
            var classifiedHandler = context.RequestServices.GetRequiredService<FeatureServerQueryHandler>();
            return await classifiedHandler.HandleGenerateClassifiedRendererAsync(
                serviceId,
                layerId,
                classificationDocument.RootElement,
                geometryType,
                context,
                cancellationToken);
        }

        var symbol = BuildSimpleSymbol(geometryType);
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

    private static Dictionary<string, object?>? BuildSimpleSymbol(MetadataV2GeometryType geometryType)
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
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => new Dictionary<string, object?>
            {
                ["type"] = "esriSMS",
                ["style"] = "esriSMSCircle",
                ["color"] = strokeColor,
                ["size"] = 6,
                ["outline"] = outline
            },
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => new Dictionary<string, object?>
            {
                ["type"] = "esriSLS",
                ["style"] = "esriSLSSolid",
                ["color"] = strokeColor,
                ["width"] = 2
            },
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => new Dictionary<string, object?>
            {
                ["type"] = "esriSFS",
                ["style"] = "esriSFSSolid",
                ["color"] = fillColor,
                ["outline"] = outline
            },
            _ => null
        };
    }

    private static bool TryParseJsonDocument(string payload, out JsonDocument? document, out string? error)
    {
        error = null;
        document = null;

        try
        {
            document = JsonDocument.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            error = InvalidRendererJsonMessage;
            return false;
        }
    }
}
