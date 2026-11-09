// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FieldWorkflows.Review;

namespace Honua.Core.Features.FieldWorkflows.Export;

public static class FieldExportFormat
{
    public const string GeoJson = "geojson";

    public const string Csv = "csv";

    public static readonly string[] All = [GeoJson, Csv];

    public static bool IsValid(string? format)
        => format is not null && Array.Exists(All, f => string.Equals(f, format, StringComparison.Ordinal));

    public static string ContentType(string format) => format switch
    {
        GeoJson => "application/geo+json",
        Csv => "text/csv; charset=utf-8",
        _ => "application/octet-stream"
    };

    public static string Extension(string format) => format switch
    {
        GeoJson => "geojson",
        Csv => "csv",
        _ => "bin"
    };
}

// An export past the bound is marked truncated and is not a complete package.
public static class FieldExportLimits
{
    public const int MaxRows = 50_000;
}

public sealed class FieldExportRequest
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = FieldExportFormat.GeoJson;

    [JsonPropertyName("formId")]
    public string? FormId { get; init; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("layerId")]
    public int? LayerId { get; init; }

    [JsonPropertyName("reviewStatus")]
    public string? ReviewStatus { get; init; }

    [JsonPropertyName("assignedTo")]
    public string? AssignedTo { get; init; }

    [JsonPropertyName("syncStatus")]
    public string? SyncStatus { get; init; }

    [JsonPropertyName("hasConflict")]
    public bool? HasConflict { get; init; }

    [JsonPropertyName("submittedFrom")]
    public DateTimeOffset? SubmittedFrom { get; init; }

    [JsonPropertyName("submittedTo")]
    public DateTimeOffset? SubmittedTo { get; init; }
}

public sealed class FieldExportRecord
{
    [JsonPropertyName("exportId")]
    public Guid ExportId { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("recordCount")]
    public long RecordCount { get; init; }

    [JsonPropertyName("filter")]
    public string Filter { get; init; } = "{}";

    [JsonPropertyName("requestedBy")]
    public string RequestedBy { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class FieldExportRecordListResult
{
    [JsonPropertyName("items")]
    public FieldExportRecord[] Items { get; init; } = [];

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }
}

public sealed class FieldExportPackage
{
    public FieldExportRecord Record { get; init; } = new();

    public FieldSubmissionRecord[] Items { get; init; } = [];

    public bool Truncated { get; init; }
}
