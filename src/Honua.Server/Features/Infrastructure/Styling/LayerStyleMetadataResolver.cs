// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Styling;

/// <summary>
/// Resolves optional GeoServices drawingInfo metadata for layer responses.
/// </summary>
internal static class LayerStyleMetadataResolver
{
    /// <summary>
    /// Attempts to resolve drawingInfo for a layer without failing the primary metadata response when styling is unavailable.
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="layer">Layer definition.</param>
    /// <param name="logger">Logger used for degraded-mode diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved drawingInfo payload or null when unavailable.</returns>
    internal static async Task<JsonElement?> TryGetDrawingInfoAsync(
        IServiceProvider services,
        LayerDefinition layer,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var serviceInspector = services.GetService<IServiceProviderIsService>();
            if (serviceInspector != null && !serviceInspector.IsService(typeof(ILayerStyleCatalog)))
            {
                return null;
            }

            var styleService = services.GetService<ILayerStyleService>();
            if (styleService is null)
            {
                return null;
            }

            return await styleService.GetDrawingInfoAsync(layer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LayerStyleLog.OptionalDrawingInfoUnavailable(logger, layer.Id, ex);
            return null;
        }
    }
}
