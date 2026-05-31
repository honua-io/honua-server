// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the Esri ImageServer <c>multidimensionalInfo</c> operation.
/// Surfaces the dimensions/variables of a multidimensional raster coverage so
/// ArcGIS clients (<c>ImageryLayer.multidimensional_info</c>) can drive
/// time/depth sliders and variable pickers.
/// </summary>
internal sealed class ImageServerMultidimensionalInfoHandler
{
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IImageServerMultidimensionalInfoBuilder _infoBuilder;
    private readonly ILogger<ImageServerMultidimensionalInfoHandler> _logger;

    public ImageServerMultidimensionalInfoHandler(
        IMetadataV2GraphProvider graphProvider,
        IImageServerMultidimensionalInfoBuilder infoBuilder,
        ILogger<ImageServerMultidimensionalInfoHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _infoBuilder = infoBuilder ?? throw new ArgumentNullException(nameof(infoBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns the Esri <c>multidimensionalInfo</c> document for a layer. When the
    /// backing layer is not a multidimensional coverage (or has no scanned
    /// metadata), a spec-correct empty document (<c>{ "variables": [] }</c>) is
    /// returned rather than a 404, matching ArcGIS behaviour for non-cube rasters.
    /// </summary>
    public async Task<IResult> GetMultidimensionalInfoAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        Activity? activity = null;

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            activity = HonuaTelemetry.StartFeatureActivity(
                "metadata",
                HonuaTelemetry.Protocols.ImageServer,
                layerId.ToString(CultureInfo.InvariantCulture));
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "multidimensional-info");

            var info = await _infoBuilder.BuildAsync(layerId, cancellationToken).ConfigureAwait(false)
                ?? new ImageServerMultidimensionalInfo();

            ImageServerLog.MultidimensionalInfoGenerated(
                _logger,
                layerId,
                info.Variables.Length,
                info.Variables.Length > 0);

            HonuaTelemetry.SetSuccess(activity, info.Variables.Length);

            var response = new MultidimensionalInfoResponse { MultidimensionalInfo = info };
            return Results.Json(response, ImageServerJsonContext.Default.MultidimensionalInfoResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.MultidimensionalInfoFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(activity, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving multidimensional information.");
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
