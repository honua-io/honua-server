// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.LocatorImport;

/// <summary>
/// Imports Esri <c>.loc</c>/<c>.lox</c> locators (definition plus reference data) into the local
/// PostGIS geocoder reference table so imported locators can be served through GeocodeServer
/// (#2152). Parsing, classification, and reference loading are shared services here so protocol
/// and admin surfaces stay thin adapters.
/// </summary>
public interface IEsriLocatorImportService
{
    /// <summary>
    /// Parses the supplied locator definition, classifies every source construct into a translation
    /// report, and (when reference data is supplied) loads the records into the configured local
    /// geocoder reference table.
    /// </summary>
    /// <param name="request">Import request (definition, optional index sidecar, optional reference data).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="EsriLocatorImportException">
    /// The request is invalid (unsupported payload, unmappable reference data, missing
    /// configuration) or the reference store rejected the load. Messages are operator-safe.
    /// </exception>
    Task<EsriLocatorImportResult> ImportAsync(EsriLocatorImportRequest request, CancellationToken cancellationToken = default);
}
