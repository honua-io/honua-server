// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Domain;

namespace Honua.Core.Features.Import.Domain;

public sealed record ImportResult
{
    public required bool Success { get; init; }

    public int FeatureCount { get; init; }

    public required string TableName { get; init; }

    // Callers must use the physical staging name rather than reconstructing the provider prefix.
    public string? PhysicalTableName { get; init; }

    public string? Schema { get; init; }

    public string SourceKind { get; init; } = "file";

    public string? SourceUrl { get; init; }

    public string? UploadId { get; init; }

    public string? CloudFileId { get; init; }

    public required SupportedFileFormat Format { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public int? DetectedSrid { get; init; }

    public int? SourceSrid { get; init; }

    public int? StoredSrid { get; init; }

    public bool Reprojected { get; init; }

    public double? StorageCrsCoordinateEpoch { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public IReadOnlyList<ImportValidationIssue> ValidationErrors { get; init; } = [];

    public int RepairedGeometryCount { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<ImportTypedColumn> TypedColumns { get; init; } = [];

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

public sealed record ImportTypedColumn
{
    public required string Name { get; init; }

    public required string PostgresType { get; init; }
}

public static class ImportValidationErrorCodes
{
    public const string EmptyDataset = "import.empty_dataset";

    public const string GeometryMissing = "import.geometry_missing";

    public const string GeometryUnknownType = "import.geometry_unknown_type";

    public const string GeometryInvalid = "import.geometry_invalid";

    public const string GeometryInvalidRepairable = "import.geometry_invalid_repairable";

    public const string GeometryTooLarge = "import.geometry_too_large";

    public const string InvalidGeoJson = "import.geojson_invalid";

    public const string GeoJsonValidationTooLarge = "import.geojson_validation_too_large";

    public const string SourceSridRequired = "import.source_srid_required";

    public const string SourceSridHeterogeneous = "import.source_srid_heterogeneous";

    public const string SourceSridUnsupported = "import.source_srid_unsupported";

    public const string TargetSridUnsupported = "import.target_srid_unsupported";

    public const string ProjectionUnsupported = "import.projection_unsupported";

    public const string CsvOptionsInvalid = "import.csv_options_invalid";

    public const string AddressGeocodeFailed = "import.address_geocode_failed";

    public const string AllRowsFailed = "import.all_rows_failed";
}

public enum ImportValidationSeverity
{
    Error = 0,

    Warning = 1,
}

public sealed record ImportValidationIssue
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public int? FeatureIndex { get; init; }

    public string? Field { get; init; }

    public ImportValidationSeverity Severity { get; init; } = ImportValidationSeverity.Error;

    public static ImportValidationIssue Create(
        string code,
        string message,
        int? featureIndex = null,
        string? field = null,
        ImportValidationSeverity severity = ImportValidationSeverity.Error) =>
        new()
        {
            Code = code,
            Message = message,
            FeatureIndex = featureIndex,
            Field = field,
            Severity = severity
        };
}
