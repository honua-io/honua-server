// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Internal;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

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

    // A layer whose dimension values cannot be enumerated returns an empty slices document rather than 404.
    public async Task<IResult> GetSlicesAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken = default)
    {
        // Intentionally not a `using` declaration: activity must stay live and undisposed
        // through the catch block below so RecordException can still attach the exception to
        // the in-flight activity. Disposing via a using-scope ending inside the try would stop
        // the activity before the catch runs, dropping exception telemetry.
        Activity? activity = null;

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByStorageLayerId(snapshot, layerId, context) is null)
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
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImageServerLog.SlicesFailed(_logger, ex, layerId);
            HonuaTelemetry.RecordException(activity, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving multidimensional slices.");
        }
        finally
        {
            DeferredDisposal.Dispose(activity);
        }
    }
}
