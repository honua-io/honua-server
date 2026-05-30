// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Internal helper that validates a raster function chain document and returns a
/// normalised description of the chain. This is intentionally not exposed as a
/// public ArcGIS ImageServer endpoint because <c>/computeClass</c> is not part of
/// the ImageServer contract.
/// </summary>
internal sealed class ImageServerAnalyzeHandler
{
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IImageServerRasterFunctionPlanner _planner;
    private readonly ILogger<ImageServerAnalyzeHandler> _logger;

    public ImageServerAnalyzeHandler(
        IMetadataV2GraphProvider graphProvider,
        IImageServerRasterFunctionPlanner planner,
        ILogger<ImageServerAnalyzeHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validates the raster function chain supplied via <c>renderingRule</c>
    /// (or <c>rasterFunction</c>) and returns the planned execution metadata.
    /// </summary>
    public async Task<IResult> AnalyzeAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "analyze-raster-function",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "analyze-raster-function");

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!IsSupportedFormat(GetString(values, "f")))
            {
                ImageServerLog.InvalidAnalyzeParameters(_logger, layerId, "Unsupported format");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Only JSON format is supported. Use f=json or f=pjson.");
            }

            var chainJson = GetString(values, "renderingRule") ?? GetString(values, "rasterFunction");
            if (string.IsNullOrWhiteSpace(chainJson))
            {
                ImageServerLog.InvalidAnalyzeParameters(_logger, layerId, "Missing renderingRule");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "renderingRule (or rasterFunction) is required and must be a JSON document.");
            }

            RasterFunctionDocument document;
            try
            {
                document = JsonSerializer.Deserialize(chainJson, ImageServerJsonContext.Default.RasterFunctionDocument)
                    ?? throw new JsonException("Empty rasterFunction document.");
            }
            catch (JsonException ex)
            {
                ImageServerLog.InvalidAnalyzeParameters(_logger, layerId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, $"Invalid raster function document: {ex.Message}");
            }

            RasterFunctionPlan plan;
            try
            {
                plan = _planner.Plan(document);
            }
            catch (ImageServerRasterFunctionException ex)
            {
                ImageServerLog.InvalidAnalyzeParameters(_logger, layerId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, ex.Message);
            }

            var response = new AnalyzeResponse
            {
                RasterFunction = document.RasterFunction,
                ChainDepth = plan.ChainDepth,
                ExecutedFunctions = plan.ExecutedFunctions,
                OutputPixelType = plan.OutputPixelType,
                Status = "success",
            };

            ImageServerLog.AnalyzeCompleted(_logger, layerId, plan.ChainDepth);
            scope.SetSuccess(plan.ChainDepth);

            return Results.Json(response, ImageServerJsonContext.Default.AnalyzeResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.AnalyzeFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while analyzing the raster function chain.");
        }
    }

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
