// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.ReferenceDataImport;

public enum ReferenceColumnStatus
{
    Supported,

    Ignored,
}

public sealed record ReferenceColumnReportEntry(string Column, ReferenceColumnStatus Status, string? Detail = null);

public sealed record GeocoderReferenceDataImportRequest
{
    public required Stream ReferenceData { get; init; }

    public IReadOnlyDictionary<string, string>? FieldMap { get; init; }

    public string? LocatorName { get; init; }

    public bool ReplaceExisting { get; init; } = true;
}

public sealed record ReferenceImportSkippedRow(int RowNumber, string Reason);

public sealed record GeocoderReferenceDataImportResult
{
    public required string LocatorName { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public int RecordsImported { get; init; }

    public int RecordsSkipped { get; init; }

    public IReadOnlyList<ReferenceImportSkippedRow> SkippedRows { get; init; } = [];

    public IReadOnlyList<ReferenceColumnReportEntry> Report { get; init; } = [];
}

public sealed class GeocoderReferenceDataImportException : Exception
{
    public GeocoderReferenceDataImportException(string message)
        : base(message)
    {
    }

    public GeocoderReferenceDataImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class GeocoderReferenceDataImportStoreException(string message, Exception innerException)
    : Exception(message, innerException);
