// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.OgcMaps.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.OgcMaps.Handlers;

/// <summary>
/// Handler for OGC API - Maps conformance operations.
/// Provides conformance class declarations for OGC Maps standards compliance.
/// </summary>
internal sealed class OgcMapsConformanceHandler
{
    private readonly ILogger<OgcMapsConformanceHandler> _logger;

    public OgcMapsConformanceHandler(ILogger<OgcMapsConformanceHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the conformance classes that this server implements for OGC API - Maps.
    /// </summary>
    public Task<OgcMapsConformance> GetConformanceAsync(CancellationToken cancellationToken = default)
    {
        OgcMapsLog.ConformanceRequested(_logger);

        var conformance = new OgcMapsConformance
        {
            ConformsTo = [
                // OGC API - Maps Part 1: Core
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core",

                // Collection maps support
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collection-map",

                // Dataset-wide maps support
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/dataset-map",

                // Collections selection (collections parameter for dataset maps)
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collections-selection",

                // Supported CRS
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/crs",

                // PNG support
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/png",

                // JPEG support
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/jpeg",

                // TIFF support
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/tiff",

                // Scaling support (width/height parameters)
                "https://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/scaling"
            ]
        };

        return Task.FromResult(conformance);
    }
}
