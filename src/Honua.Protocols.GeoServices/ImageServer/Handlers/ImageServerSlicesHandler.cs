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
/// Handler for the Esri ImageServer <c>slices</c> operation. Enumerates the discrete
/// multidimensional slices (variable + dimension coordinate combinations) of a
/// multidimensional raster coverage so ArcGIS clients can iterate slices.
/// </summary>
internal sealed class ImageServerSlicesHandler
{
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IImageServerMultidimensionalInfoBuilder _infoBuilder;
    private readonly ILogger<ImageServerSlicesHandler> _logger;

    public ImageServerSlicesHandler(
        IMetadataV2GraphProvider graphProvider,
        IImageServerMultidimensionalInfoBuilder infoBuilder,
        ILogger<ImageServerSlicesHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _infoBuilder = infoBuilder ?? throw new ArgumentNullException(nameof(infoBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns the Esri <c>slices</c> document for a layer. When the backing layer is
    /// not a multidimensional coverage, has no scanned metadata, or its dimension
    /// coordinate values are not enumerable, a spec-correct empty document
    /// (<c>{ "slices": [] }</c>) is returned rather than a 404.
    /// </summary>
    public async Task<IResult> GetSlicesAsync(
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
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "slices");

            var info = await _infoBuilder.BuildAsync(layerId, cancellationToken).ConfigureAwait(false)
                ?? new ImageServerMultidimensionalInfo();

            var slices = ImageServerSlicesBuilder.Build(info);

            HonuaTelemetry.SetSuccess(activity, slices.Length);

            var response = new SlicesResponse { Slices = slices };
            return Results.Json(response, ImageServerJsonContext.Default.SlicesResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.SlicesFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(activity, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving multidimensional slices.");
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
