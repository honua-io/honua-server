// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// A coordinate resolved by a <see cref="CsvAddressGeocoder"/> for a CSV row's
/// address value, expressed as longitude/latitude in the import's source
/// spatial reference (WGS84 lon/lat by default).
/// </summary>
/// <param name="Longitude">Longitude (X) ordinate of the resolved location.</param>
/// <param name="Latitude">Latitude (Y) ordinate of the resolved location.</param>
public sealed record CsvGeocodedAddress(double Longitude, double Latitude);

/// <summary>
/// Caller-supplied hook that resolves a freeform address value from a CSV row
/// into a coordinate. The import pipeline stays geocoder-agnostic: callers that
/// want address-driven geometry (for example the MCP ingest tool routing through
/// the canonical geocoding coordinator) inject their resolver here. Returning
/// <see langword="null"/> marks the row as not geocodable; the row is still
/// imported (attributes only) and the failure is surfaced as a per-row
/// validation issue.
/// </summary>
/// <param name="address">The non-empty address text from the configured address column.</param>
/// <param name="cancellationToken">Cancellation token for the import operation.</param>
/// <returns>The resolved coordinate, or <see langword="null"/> when the address could not be resolved.</returns>
public delegate Task<CsvGeocodedAddress?> CsvAddressGeocoder(string address, CancellationToken cancellationToken);

/// <summary>
/// Optional CSV-specific options on an <see cref="ImportRequest"/>. When any
/// explicit column is set these options replace the CSV reader's header
/// heuristics (auto-detected lon/lng/longitude/x, lat/latitude/y, and WKT
/// column names); when the record is omitted the historical auto-detection
/// behavior is unchanged.
/// </summary>
public sealed record CsvImportOptions
{
    /// <summary>
    /// Explicit longitude (X) column name. Must be paired with
    /// <see cref="LatitudeColumn"/>; mutually exclusive with <see cref="AddressColumn"/>.
    /// Matched case-insensitively against the CSV header row.
    /// </summary>
    public string? LongitudeColumn { get; init; }

    /// <summary>
    /// Explicit latitude (Y) column name. Must be paired with
    /// <see cref="LongitudeColumn"/>; mutually exclusive with <see cref="AddressColumn"/>.
    /// Matched case-insensitively against the CSV header row.
    /// </summary>
    public string? LatitudeColumn { get; init; }

    /// <summary>
    /// Column holding freeform addresses to resolve into point geometries via
    /// <see cref="AddressGeocoder"/>. Mutually exclusive with the explicit
    /// coordinate columns. The address value is preserved as a row attribute.
    /// </summary>
    public string? AddressColumn { get; init; }

    /// <summary>
    /// Resolver invoked once per row when <see cref="AddressColumn"/> is set.
    /// Required when an address column is configured.
    /// </summary>
    public CsvAddressGeocoder? AddressGeocoder { get; init; }

    /// <summary>
    /// Upper bound on the number of rows that may be geocoded in one import.
    /// Exceeding the cap fails the import with
    /// <see cref="ImportValidationErrorCodes.CsvOptionsInvalid"/> so callers can
    /// redirect oversized address datasets to a batch geocoding surface.
    /// </summary>
    public int? MaxGeocodedRows { get; init; }
}

/// <summary>
/// Thrown by the CSV import path when <see cref="CsvImportOptions"/> cannot be
/// applied to the source document (missing/ambiguous columns, conflicting
/// options, or a geocoded-row cap overrun). Mapped by the import service to a
/// failed <see cref="ImportResult"/> carrying
/// <see cref="ImportValidationErrorCodes.CsvOptionsInvalid"/>. Messages are
/// composed from caller-supplied option values and CSV header names only, so
/// they are safe to surface to clients.
/// </summary>
public sealed class CsvImportOptionsException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CsvImportOptionsException"/> class.
    /// </summary>
    /// <param name="message">Client-safe description of the option problem.</param>
    public CsvImportOptionsException(string message)
        : base(message)
    {
    }
}
