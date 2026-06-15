// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    private const string InvalidDynamicLayerJsonMessage = "dynamicLayer contains invalid JSON.";

    private sealed record DynamicLayerResourceDefinition(
        int Id,
        int MapLayerId,
        string? DefinitionExpression,
        string? DrawingInfoJson,
        string? Name);

    /// <summary>
    /// Handle MapServer dynamicLayer metadata resource requests.
    /// </summary>
    private static async Task<IResult> HandleDynamicLayerResource(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.MapServer,
            "dynamicLayer");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "get-dynamic-layer-metadata");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveMapServerMetadataLayers(snapshot, service);
            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            var sourceResolver = CreateDynamicLayerSourceResolver(
                context,
                snapshot,
                publishedLayers.Select(static layer => new DynamicLayerCandidate(layer.PublicLayerId, layer.Resource)));

            var layerValue = context.Request.Query.TryGetValue("layer", out var layerValues)
                ? layerValues.ToString()
                : null;
            if (!TryParseDynamicLayerResource(
                    layerValue,
                    sourceResolver,
                    queryValidator,
                    out var dynamicLayer,
                    out var dynamicLayerError))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    dynamicLayerError ?? "Invalid dynamicLayer layer parameter.");
            }

            var parsedDynamicLayer = dynamicLayer!;
            var sourceLayer = publishedLayers.FirstOrDefault(layer => layer.PublicLayerId == parsedDynamicLayer.MapLayerId);
            if (sourceLayer is null)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"dynamicLayer references unknown layer '{parsedDynamicLayer.MapLayerId}'.");
            }

            var accessError = AccessPolicyHelpers.RequireResourceAccess(context, sourceLayer.Resource, service);
            if (accessError != null)
            {
                return accessError;
            }

            var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
            var drawingInfo = ResolveDynamicLayerDrawingInfo(
                sourceLayer.Resource,
                snapshot,
                parsedDynamicLayer.DrawingInfoJson);
            var response = MapLayerToMapServerLayerResponse(
                service,
                sourceLayer.Publication,
                sourceLayer.Resource,
                snapshot,
                limitsOptions.Query.MaxRecordCount,
                drawingInfo,
                parsedDynamicLayer.Id,
                parsedDynamicLayer.Name,
                parsedDynamicLayer.DefinitionExpression);

            stopwatch.Stop();
            scope.SetSuccess(1);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, MapServerJsonContext.Default.MapServerLayerResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }

    private static bool TryParseDynamicLayerResource(
        string? layerValue,
        DynamicLayerSourceResolver sourceResolver,
        ICommonQueryValidator queryValidator,
        out DynamicLayerResourceDefinition? dynamicLayer,
        out string? error)
    {
        dynamicLayer = null;
        error = null;

        if (string.IsNullOrWhiteSpace(layerValue))
        {
            error = "dynamicLayer requires a layer parameter.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(layerValue);
            var element = document.RootElement;
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = "dynamicLayer layer parameter must be a JSON object.";
                return false;
            }

            if (!element.TryGetProperty("source", out var sourceElement) ||
                sourceElement.ValueKind != JsonValueKind.Object)
            {
                error = "dynamicLayer must include a source object.";
                return false;
            }

            if (!sourceResolver.TryResolveMapLayerId(sourceElement, "dynamicLayer", out var mapLayerId, out error))
            {
                return false;
            }

            var id = TryGetJsonInt(element, "id", out var requestedId)
                ? requestedId
                : mapLayerId;
            var layerDefinition = ResolveDynamicLayerDefinitionElement(element);
            if (!TryResolveDynamicLayerDefinitionExpression(
                    element,
                    layerDefinition,
                    id,
                    queryValidator,
                    out var definitionExpression,
                    out error))
            {
                return false;
            }

            if (!TryResolveDynamicLayerDrawingInfoJson(
                    element,
                    layerDefinition,
                    id,
                    out var drawingInfoJson,
                    out error))
            {
                return false;
            }

            var layerName = ResolveDynamicLayerName(element, layerDefinition);
            dynamicLayer = new DynamicLayerResourceDefinition(
                id,
                mapLayerId,
                string.IsNullOrWhiteSpace(definitionExpression) ? null : definitionExpression,
                string.IsNullOrWhiteSpace(drawingInfoJson) ? null : drawingInfoJson,
                string.IsNullOrWhiteSpace(layerName) ? null : layerName);
            return true;
        }
        catch (JsonException)
        {
            error = InvalidDynamicLayerJsonMessage;
            return false;
        }
    }

    private static JsonElement? ResolveDynamicLayerDefinitionElement(JsonElement element)
    {
        if (element.TryGetProperty("layerDefinition", out var layerDefinition) &&
            layerDefinition.ValueKind == JsonValueKind.Object)
        {
            return layerDefinition;
        }

        return null;
    }

    private static bool TryResolveDynamicLayerDefinitionExpression(
        JsonElement element,
        JsonElement? layerDefinition,
        int dynamicLayerId,
        ICommonQueryValidator queryValidator,
        out string? definitionExpression,
        out string? error)
    {
        definitionExpression = null;
        error = null;

        if (element.TryGetProperty("definitionExpression", out var definitionElement))
        {
            if (!TryReadJsonStringOrNumber(definitionElement, out definitionExpression))
            {
                error = $"dynamicLayer '{dynamicLayerId}' has an invalid definitionExpression.";
                return false;
            }
        }

        if (definitionExpression == null &&
            layerDefinition is { } definition &&
            definition.TryGetProperty("definitionExpression", out var nestedDefinition))
        {
            if (!TryReadJsonStringOrNumber(nestedDefinition, out definitionExpression))
            {
                error = $"dynamicLayer '{dynamicLayerId}' has an invalid layerDefinition.definitionExpression.";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(definitionExpression))
        {
            var validation = queryValidator.ValidateWhereClause(definitionExpression);
            if (!validation.IsValid)
            {
                error = validation.ErrorMessage ?? $"Invalid definitionExpression for dynamicLayer '{dynamicLayerId}'.";
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveDynamicLayerDrawingInfoJson(
        JsonElement element,
        JsonElement? layerDefinition,
        int dynamicLayerId,
        out string? drawingInfoJson,
        out string? error)
    {
        drawingInfoJson = null;
        error = null;

        if (element.TryGetProperty("drawingInfo", out var drawingInfoElement))
        {
            if (drawingInfoElement.ValueKind != JsonValueKind.Object)
            {
                error = $"dynamicLayer '{dynamicLayerId}' has an invalid drawingInfo object.";
                return false;
            }

            drawingInfoJson = drawingInfoElement.GetRawText();
            return true;
        }

        if (layerDefinition is { } definition &&
            definition.TryGetProperty("drawingInfo", out var nestedDrawingInfo))
        {
            if (nestedDrawingInfo.ValueKind != JsonValueKind.Object)
            {
                error = $"dynamicLayer '{dynamicLayerId}' has an invalid layerDefinition.drawingInfo object.";
                return false;
            }

            drawingInfoJson = nestedDrawingInfo.GetRawText();
        }

        return true;
    }

    private static string? ResolveDynamicLayerName(JsonElement element, JsonElement? layerDefinition)
    {
        if (TryGetJsonString(element, "name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return layerDefinition is { } definition &&
               TryGetJsonString(definition, "name", out var nestedName) &&
               !string.IsNullOrWhiteSpace(nestedName)
            ? nestedName
            : null;
    }

    private static JsonElement? ResolveDynamicLayerDrawingInfo(
        MetadataV2Resource sourceResource,
        MetadataV2GraphSnapshot snapshot,
        string? drawingInfoJson)
    {
        if (!string.IsNullOrWhiteSpace(drawingInfoJson) &&
            TryParseMapServerJsonElement(drawingInfoJson, out var drawingInfo) &&
            drawingInfo.ValueKind == JsonValueKind.Object)
        {
            return drawingInfo;
        }

        return ResolveMapServerDrawingInfo(sourceResource, snapshot);
    }
}
