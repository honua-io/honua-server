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
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core",

                // OGC API - Common conformance classes
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/oas30",

                // Collection maps support
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collection-map",

                // Dataset maps support
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/dataset-map",

                // Styled maps support
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/styled-map",

                // Supported CRS
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/crs",

                // Spatial subsetting (bbox parameter)
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/bbox",

                // PNG support
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/png",

                // JPEG support
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/jpeg",

                // TIFF support
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/tiff",

                // Scaling support (width/height parameters)
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/scaling",

                // Background parameter support
                "http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/background"
            ]
        };

        return Task.FromResult(conformance);
    }
}
