// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Domain;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Result of a file import operation
/// </summary>
public sealed record ImportResult
{
    /// <summary>
    /// Whether the import was successful
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of features imported
    /// </summary>
    public int FeatureCount { get; init; }

    /// <summary>
    /// Table name created/updated
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Physical staging table name actually created in the database (provider-prefixed,
    /// e.g. <c>imported_&lt;table&gt;</c>). Distinct from <see cref="TableName"/>, which is
    /// the caller-supplied logical name. Callers that need to introspect or publish the
    /// imported table (for example the durable import GP job) must use this name rather than
    /// reconstructing the provider naming convention. Falls back to <see cref="TableName"/>
    /// when the provider does not stage into a separate physical table.
    /// </summary>
    public string? PhysicalTableName { get; init; }

    /// <summary>
    /// Schema that owns the physical staging table (provider operational-data schema,
    /// e.g. <c>honua_data</c>). Pair with <see cref="PhysicalTableName"/> to locate the
    /// imported table.
    /// </summary>
    public string? Schema { get; init; }

    /// <summary>
    /// Stable source kind for admin import result views.
    /// </summary>
    public string SourceKind { get; init; } = "file";

    /// <summary>
    /// Source URL for URL-based imports.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Upload operation ID associated with this import, when known.
    /// </summary>
    public string? UploadId { get; init; }

    /// <summary>
    /// Cloud storage file ID when the source was staged to object storage.
    /// </summary>
    public string? CloudFileId { get; init; }

    /// <summary>
    /// Detected file format
    /// </summary>
    public required SupportedFileFormat Format { get; init; }

    /// <summary>
    /// Detected coordinate reference system ID
    /// </summary>
    public int? DetectedSrid { get; init; }

    /// <summary>
    /// Error message if import failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Stable machine-readable error code if import failed.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Structured validation issues that blocked import before rows were created.
    /// </summary>
    public IReadOnlyList<ImportValidationIssue> ValidationErrors { get; init; } = [];

    /// <summary>
    /// Number of features whose geometry was invalid on input and was automatically
    /// repaired (ST_MakeValid-equivalent) before insertion under the shared import
    /// validity gate. Zero when the gate is in <c>Accept</c>/<c>Strict</c> mode or no
    /// input geometry required repair.
    /// </summary>
    public int RepairedGeometryCount { get; init; }

    /// <summary>
    /// Import duration
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Warning messages surfaced during import.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Create successful import result
    /// </summary>
    public static ImportResult CreateSuccess(
        string tableName,
        SupportedFileFormat format,
        int featureCount,
        int? detectedSrid = null,
        TimeSpan duration = default,
        IReadOnlyList<string>? warnings = null,
        string? physicalTableName = null,
        string? schema = null,
        int repairedGeometryCount = 0) =>
        new()
        {
            Success = true,
            TableName = tableName,
            PhysicalTableName = physicalTableName ?? tableName,
            Schema = schema,
            Format = format,
            FeatureCount = featureCount,
            DetectedSrid = detectedSrid,
            RepairedGeometryCount = repairedGeometryCount,
            Duration = duration,
            Warnings = warnings ?? []
        };

    /// <summary>
    /// Create failed import result
    /// </summary>
    public static ImportResult CreateFailure(
        string tableName,
        SupportedFileFormat format,
        string errorMessage,
        TimeSpan duration = default,
        IReadOnlyList<string>? warnings = null,
        string? errorCode = null,
        IReadOnlyList<ImportValidationIssue>? validationErrors = null) =>
        new()
        {
            Success = false,
            TableName = tableName,
            Format = format,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
            Duration = duration,
            Warnings = warnings ?? [],
            ValidationErrors = validationErrors ?? []
        };
}

/// <summary>
/// Stable import validation error codes surfaced in import failure responses.
/// </summary>
public static class ImportValidationErrorCodes
{
    /// <summary>The source dataset contained no features.</summary>
    public const string EmptyDataset = "import.empty_dataset";

    /// <summary>A feature was missing required geometry.</summary>
    public const string GeometryMissing = "import.geometry_missing";

    /// <summary>A feature had an unrecognized geometry type.</summary>
    public const string GeometryUnknownType = "import.geometry_unknown_type";

    /// <summary>A feature had invalid geometry.</summary>
    public const string GeometryInvalid = "import.geometry_invalid";

    /// <summary>
    /// A single feature's geometry exceeded the configured size guard (vertices, rings, or WKB
    /// bytes). The geometry is too large to materialize safely; explode/simplify it before import.
    /// Surfaced 413-style so callers can react programmatically (#1626).
    /// </summary>
    public const string GeometryTooLarge = "import.geometry_too_large";

    /// <summary>The source GeoJSON document was invalid.</summary>
    public const string InvalidGeoJson = "import.geojson_invalid";

    /// <summary>The GeoJSON document exceeded the validation size limit.</summary>
    public const string GeoJsonValidationTooLarge = "import.geojson_validation_too_large";

    /// <summary>A source spatial reference identifier was required but not provided.</summary>
    public const string SourceSridRequired = "import.source_srid_required";

    /// <summary>The supplied source spatial reference identifier is not supported.</summary>
    public const string SourceSridUnsupported = "import.source_srid_unsupported";

    /// <summary>The requested target spatial reference identifier is not supported.</summary>
    public const string TargetSridUnsupported = "import.target_srid_unsupported";

    /// <summary>The requested projection transformation is not supported.</summary>
    public const string ProjectionUnsupported = "import.projection_unsupported";

    /// <summary>
    /// The supplied CSV import options could not be applied (missing/conflicting
    /// columns or a geocoded-row cap overrun).
    /// </summary>
    public const string CsvOptionsInvalid = "import.csv_options_invalid";

    /// <summary>
    /// A CSV row's address value could not be geocoded; the row was imported
    /// without geometry.
    /// </summary>
    public const string AddressGeocodeFailed = "import.address_geocode_failed";
}

/// <summary>
/// Machine-readable validation issue for file import failures.
/// </summary>
public sealed record ImportValidationIssue
{
    /// <summary>Gets the stable machine-readable validation error code.</summary>
    public required string Code { get; init; }

    /// <summary>Gets the human-readable validation message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the zero-based index of the offending feature, when known.</summary>
    public int? FeatureIndex { get; init; }

    /// <summary>Gets the name of the offending field, when known.</summary>
    public string? Field { get; init; }

    /// <summary>
    /// Creates an <see cref="ImportValidationIssue"/> with the supplied details.
    /// </summary>
    /// <param name="code">The stable machine-readable validation error code.</param>
    /// <param name="message">The human-readable validation message.</param>
    /// <param name="featureIndex">The zero-based index of the offending feature, when known.</param>
    /// <param name="field">The name of the offending field, when known.</param>
    /// <returns>A populated <see cref="ImportValidationIssue"/>.</returns>
    public static ImportValidationIssue Create(
        string code,
        string message,
        int? featureIndex = null,
        string? field = null) =>
        new()
        {
            Code = code,
            Message = message,
            FeatureIndex = featureIndex,
            Field = field
        };
}
