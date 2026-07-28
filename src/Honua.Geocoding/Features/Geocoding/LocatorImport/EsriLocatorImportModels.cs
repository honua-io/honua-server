// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.LocatorImport;

/// <summary>
/// Translation outcome for a single construct found in an imported Esri locator (#2152).
/// Every property in the source <c>.loc</c> file lands in exactly one of these buckets so
/// nothing is silently dropped.
/// </summary>
public enum LocatorTranslationStatus
{
    /// <summary>The construct was understood and carried into the imported locator.</summary>
    Supported,

    /// <summary>
    /// The construct has no equivalent in the local geocoder and was not applied. Unsupported
    /// entries are reported explicitly rather than silently dropped.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The construct is a derived artifact (for example the binary <c>.lox</c> index) that is
    /// not read; the equivalent structure is rebuilt in PostGIS during import.
    /// </summary>
    Regenerated,

    /// <summary>
    /// The construct is intentionally not applicable to this import path (for example reference
    /// data pointers, because reference data is supplied directly in the import request).
    /// </summary>
    Ignored,
}

/// <summary>
/// A single entry in the locator import translation report.
/// </summary>
/// <param name="Item">The source construct (a <c>.loc</c> property key, file name, or CSV column).</param>
/// <param name="Status">How the construct was translated.</param>
/// <param name="Detail">Optional human-readable explanation.</param>
public sealed record LocatorTranslationEntry(string Item, LocatorTranslationStatus Status, string? Detail = null);

/// <summary>
/// Match/candidate tuning settings parsed from a classic Esri locator definition. Values are
/// recorded verbatim from the source locator; applying them to runtime candidate scoring is
/// tracked separately (see the translation report).
/// </summary>
public sealed record EsriLocatorMatchSettings
{
    /// <summary>Minimum score a candidate needs to be considered a match (0-100).</summary>
    public double? MinimumMatchScore { get; init; }

    /// <summary>Minimum score a candidate needs to be returned at all (0-100).</summary>
    public double? MinimumCandidateScore { get; init; }

    /// <summary>Spelling sensitivity of the source locator (0-100).</summary>
    public double? SpellingSensitivity { get; init; }

    /// <summary>Side offset applied when placing address-range matches.</summary>
    public double? SideOffset { get; init; }

    /// <summary>Units of <see cref="SideOffset"/> (for example <c>Feet</c> or <c>Meters</c>).</summary>
    public string? SideOffsetUnits { get; init; }

    /// <summary>End offset percentage applied to interpolated street matches.</summary>
    public double? EndOffset { get; init; }

    /// <summary>Whether the source locator matched when multiple candidates tie on score.</summary>
    public bool? MatchIfScoresTie { get; init; }

    /// <summary>Whether the source locator interpolated along address ranges.</summary>
    public bool? Interpolate { get; init; }
}

/// <summary>
/// Parsed representation of a classic (text, key = value) Esri <c>.loc</c> locator definition.
/// </summary>
public sealed record EsriLocatorDefinition
{
    /// <summary>Locator name (derived from the uploaded file name unless overridden).</summary>
    public required string Name { get; init; }

    /// <summary>Locator definition version (the <c>Version</c> property), when present.</summary>
    public string? Version { get; init; }

    /// <summary>Locator style class id (the <c>CLSID</c> property), when present.</summary>
    public string? StyleId { get; init; }

    /// <summary>Locator category (the <c>Category</c>/<c>Categories</c> property), when present.</summary>
    public string? Category { get; init; }

    /// <summary>Input field metadata recorded from <c>Fields</c> properties.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Match settings recorded from the source locator.</summary>
    public EsriLocatorMatchSettings MatchSettings { get; init; } = new();
}

/// <summary>
/// Request for importing an Esri <c>.loc</c>/<c>.lox</c> locator plus its reference data into
/// the local PostGIS geocoder (#2152).
/// </summary>
public sealed record EsriLocatorImportRequest
{
    /// <summary>File name of the uploaded <c>.loc</c> definition (used for the default locator name).</summary>
    public required string LocFileName { get; init; }

    /// <summary>Raw content of the uploaded <c>.loc</c> definition.</summary>
    public required ReadOnlyMemory<byte> LocContent { get; init; }

    /// <summary>File name of the optional <c>.lox</c> index sidecar, when supplied.</summary>
    public string? IndexFileName { get; init; }

    /// <summary>
    /// Reference data records (CSV with a header row) to load into the geocoder reference table.
    /// When omitted the locator definition is parsed and classified without loading any data.
    /// The caller owns the stream lifetime.
    /// </summary>
    public Stream? ReferenceData { get; init; }

    /// <summary>
    /// Optional explicit mapping of canonical reference roles (<c>displayName</c>,
    /// <c>addressNumber</c>, <c>streetName</c>, <c>city</c>, <c>region</c>, <c>postalCode</c>,
    /// <c>country</c>, <c>neighborhood</c>, <c>addressType</c>, <c>x</c>, <c>y</c>) to CSV column
    /// names. Roles not listed fall back to well-known Esri reference field name aliases.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FieldMap { get; init; }

    /// <summary>Optional locator name override; defaults to the <c>.loc</c> file base name.</summary>
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
public sealed record LocatorImportSkippedRow(int RowNumber, string Reason);

/// <summary>
/// Result of an Esri locator import: the parsed definition, load counts, and the translation
/// report covering every source construct.
/// </summary>
public sealed record EsriLocatorImportResult
{
    /// <summary>Parsed locator definition.</summary>
    public required EsriLocatorDefinition Definition { get; init; }

    /// <summary>Schema of the target reference table.</summary>
    public required string Schema { get; init; }

    /// <summary>Name of the target reference table.</summary>
    public required string Table { get; init; }

    /// <summary>Whether reference data was supplied and loaded.</summary>
    public required bool ReferenceDataImported { get; init; }

    /// <summary>Number of reference rows written to the reference table.</summary>
    public int RecordsImported { get; init; }

    /// <summary>Number of reference rows skipped (invalid coordinates, empty address, ...).</summary>
    public int RecordsSkipped { get; init; }

    /// <summary>Detail for skipped rows (capped; <see cref="RecordsSkipped"/> is the full count).</summary>
    public IReadOnlyList<LocatorImportSkippedRow> SkippedRows { get; init; } = [];

    /// <summary>
    /// Translation report covering every property of the source locator, the index sidecar, and
    /// every reference CSV column. Unsupported constructs appear here explicitly.
    /// </summary>
    public IReadOnlyList<LocatorTranslationEntry> Report { get; init; } = [];
}

/// <summary>
/// Raised when an Esri locator import request is invalid or cannot be completed. Messages are
/// operator-safe (no connection strings, SQL, or provider internals) and can be surfaced to
/// admin API clients.
/// </summary>
public sealed class EsriLocatorImportException : Exception
{
    /// <summary>Initializes a new instance with an operator-safe message.</summary>
    /// <param name="message">Operator-safe error message.</param>
    public EsriLocatorImportException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with an operator-safe message and an inner exception.</summary>
    /// <param name="message">Operator-safe error message.</param>
    /// <param name="innerException">Underlying failure (not exposed to clients).</param>
    public EsriLocatorImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
