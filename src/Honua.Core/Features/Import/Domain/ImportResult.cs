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
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = true,
            TableName = tableName,
            Format = format,
            FeatureCount = featureCount,
            DetectedSrid = detectedSrid,
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
