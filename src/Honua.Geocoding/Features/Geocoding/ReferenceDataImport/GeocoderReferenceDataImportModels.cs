// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.ReferenceDataImport;

/// <summary>
/// Translation outcome for a single CSV column in a geocoder reference data import. Every header
/// column lands in exactly one of these buckets so nothing is silently dropped.
/// </summary>
public enum ReferenceColumnStatus
{
    /// <summary>The column was mapped to a geocoder reference field and its values are imported.</summary>
    Supported,

    /// <summary>
    /// The column is not mapped to any reference field and its values are not imported. Ignored
    /// columns are reported explicitly rather than silently dropped.
    /// </summary>
    Ignored,
}

/// <summary>
/// A single entry in the reference data import column report.
/// </summary>
/// <param name="Column">The CSV header column name.</param>
/// <param name="Status">How the column was handled.</param>
/// <param name="Detail">Optional human-readable explanation (for example the mapped roles).</param>
public sealed record ReferenceColumnReportEntry(string Column, ReferenceColumnStatus Status, string? Detail = null);

/// <summary>
/// Request for importing CSV reference data into the local PostGIS geocoder so the records can be
/// served through GeocodeServer by the <c>local</c> provider.
/// </summary>
public sealed record GeocoderReferenceDataImportRequest
{
    /// <summary>
    /// Reference data records as CSV with a header row. The caller owns the stream lifetime.
    /// </summary>
    public required Stream ReferenceData { get; init; }

    /// <summary>
    /// Optional explicit mapping of canonical reference roles (<c>displayName</c>,
    /// <c>addressNumber</c>, <c>streetName</c>, <c>city</c>, <c>region</c>, <c>postalCode</c>,
    /// <c>country</c>, <c>neighborhood</c>, <c>addressType</c>, <c>x</c>, <c>y</c>) to CSV column
    /// names. Roles not listed fall back to well-known reference field name aliases.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FieldMap { get; init; }

    /// <summary>
    /// Optional locator (geocode service) name the import is intended for; it must match the
    /// configured <c>Geocoding:LocatorName</c> the server registers. Defaults to that name.
    /// </summary>
    public string? LocatorName { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default) existing rows in the reference table are removed
    /// before loading; otherwise imported rows are appended.
    /// </summary>
    public bool ReplaceExisting { get; init; } = true;
}

/// <summary>
/// A reference data row that was skipped during import, with the reason.
/// </summary>
/// <param name="RowNumber">1-based data row number (excluding the header row).</param>
/// <param name="Reason">Why the row was skipped.</param>
public sealed record ReferenceImportSkippedRow(int RowNumber, string Reason);

/// <summary>
/// Result of a geocoder reference data import: load counts, skipped-row detail, and the column
/// report covering every CSV header column.
/// </summary>
public sealed record GeocoderReferenceDataImportResult
{
    /// <summary>Locator (geocode service) name the imported data is served under.</summary>
    public required string LocatorName { get; init; }

    /// <summary>Schema of the target reference table.</summary>
    public required string Schema { get; init; }

    /// <summary>Name of the target reference table.</summary>
    public required string Table { get; init; }

    /// <summary>Number of reference rows written to the reference table.</summary>
    public int RecordsImported { get; init; }

    /// <summary>Number of reference rows skipped (invalid coordinates, empty address, ...).</summary>
    public int RecordsSkipped { get; init; }

    /// <summary>Detail for skipped rows (capped; <see cref="RecordsSkipped"/> is the full count).</summary>
    public IReadOnlyList<ReferenceImportSkippedRow> SkippedRows { get; init; } = [];

    /// <summary>
    /// Column report covering every CSV header column: mapped columns and their roles, and every
    /// ignored column, reported explicitly.
    /// </summary>
    public IReadOnlyList<ReferenceColumnReportEntry> Report { get; init; } = [];
}

/// <summary>
/// Raised when a geocoder reference data import request is invalid (unmappable CSV, bad field map,
/// missing configuration). Messages are operator-safe (no connection strings, SQL, or provider
/// internals) and can be surfaced to admin API clients as client faults.
/// </summary>
public sealed class GeocoderReferenceDataImportException : Exception
{
    /// <summary>Initializes a new instance with an operator-safe message.</summary>
    /// <param name="message">Operator-safe error message.</param>
    public GeocoderReferenceDataImportException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with an operator-safe message and an inner exception.</summary>
    /// <param name="message">Operator-safe error message.</param>
    /// <param name="innerException">Underlying failure (not exposed to clients).</param>
    public GeocoderReferenceDataImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when the geocoder reference store itself fails during a reference data import (database
/// unavailable, permission or schema problems). Distinct from
/// <see cref="GeocoderReferenceDataImportException"/> so adapters can surface a 5xx server error
/// instead of misclassifying it as invalid client input.
/// </summary>
/// <param name="message">Operator-safe error message.</param>
/// <param name="innerException">Underlying failure (not exposed to clients).</param>
public sealed class GeocoderReferenceDataImportStoreException(string message, Exception innerException)
    : Exception(message, innerException);
