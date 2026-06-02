// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.Ogc.Api.Styles.Models;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.Ogc.Api.Styles.Handlers;

/// <summary>
/// Handler for OGC API - Styles conformance declarations. Declares the conformance
/// classes implemented by the Phase 1 adapter (ADR-0048): core, mapbox-styles,
/// sld-10, sld-11, style-validation, and (partial) manage-styles.
/// </summary>
internal sealed class OgcStylesConformanceHandler
{
    private const string ConformanceBase = "http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/";

    private readonly ILogger<OgcStylesConformanceHandler> _logger;

    public OgcStylesConformanceHandler(ILogger<OgcStylesConformanceHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the conformance classes that this server implements for OGC API - Styles.
    /// </summary>
    public Task<OgcStylesConformance> GetConformanceAsync(CancellationToken cancellationToken = default)
    {
        OgcStylesLog.ConformanceRequested(_logger);

        var conformance = new OgcStylesConformance
        {
            ConformsTo =
            [
                // OGC API - Styles Part 1: Core
                ConformanceBase + "core",

                // Mapbox/MapLibre style encoding (canonical store)
                ConformanceBase + "mapbox-styles",

                // SLD 1.0 encoding (derived from the canonical MapLibre style)
                ConformanceBase + "sld-10",

                // SLD 1.1 encoding (derived from the canonical MapLibre style)
                ConformanceBase + "sld-11",

                // Style validation (Prefer: handling=strict / ?validate)
                ConformanceBase + "style-validation",

                // Manage styles - PARTIAL in Phase 1 (PUT only; POST/DELETE arrive with
                // the Phase 2 independent style catalog, issue #1389).
                ConformanceBase + "manage-styles"
            ]
        };

        return Task.FromResult(conformance);
    }
}
