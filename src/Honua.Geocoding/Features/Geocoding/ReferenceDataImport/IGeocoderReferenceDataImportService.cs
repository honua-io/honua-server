// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.ReferenceDataImport;

/// <summary>
/// Imports CSV reference data into the local PostGIS geocoder reference table so the records can
/// be served through GeocodeServer by the <c>local</c> provider (#2151). Column mapping,
/// validation, and loading are shared services here so protocol and admin surfaces stay thin
/// adapters.
/// </summary>
public interface IGeocoderReferenceDataImportService
{
    /// <summary>
    /// Maps the CSV columns to the canonical reference roles (explicit field map first, then
    /// well-known field name aliases), classifies every header column into an explicit report,
    /// and loads the records into the configured local geocoder reference table.
    /// </summary>
    /// <param name="request">Import request (CSV reference data, optional field map, mode).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="GeocoderReferenceDataImportException">
    /// The request is invalid (unmappable reference data, bad field map, missing configuration).
    /// Messages are operator-safe.
    /// </exception>
    /// <exception cref="GeocoderReferenceDataImportStoreException">
    /// The reference store itself failed (database unavailable, permissions, schema problems).
    /// </exception>
    Task<GeocoderReferenceDataImportResult> ImportAsync(
        GeocoderReferenceDataImportRequest request,
        CancellationToken cancellationToken = default);
}
