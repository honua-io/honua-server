// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Exports;

// One Export version as Honua models it. It is stored only as its STAC collection document (ExportCollectionDocument).
public sealed record ExportVersion
{
    public required string ExportId { get; init; }

    public required string Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public required ExportSourceLayer SourceLayer { get; init; }

    public required ExportCapture Capture { get; init; }

    public required double[] Bounds { get; init; }

    public required ExportCrs Crs { get; init; }

    public required IReadOnlyList<ExportVersionField> Fields { get; init; }

    public required ExportIdentity Identity { get; init; }

    public required ExportGeometry Geometry { get; init; }

    public required ExportArtifacts Artifacts { get; init; }

    public required ExportValidation Validation { get; init; }
}

public sealed record ExportSourceLayer(string Service, string Layer)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
}

public sealed record ExportCapture(string CaptureId, DateTimeOffset CapturedAt, string SourceRevision, long FeatureCount);

public sealed record ExportCrs(string Source, string Geoparquet)
{
    public string Pmtiles { get; init; } = "EPSG:3857";
}

public sealed record ExportVersionField(string Name, string Type, bool Public)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}

public sealed record ExportIdentity(string Field, string PmtilesProperty, string GeoparquetColumn)
{
    public string Type { get; init; } = "string";

    // True when the field is the Layer's declared text key. False when it is the object id rendered as text.
    public bool Stable { get; init; } = true;
}

public sealed record ExportGeometry(string Column, string Encoding, IReadOnlyList<string> Types)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BboxColumn { get; init; }
}

public sealed record ExportArtifacts(PmtilesExportArtifact Pmtiles, GeoparquetExportArtifact Geoparquet);

public sealed record PmtilesExportArtifact
{
    public const string MediaTypeValue = "application/vnd.pmtiles";

    public required string Href { get; init; }

    public string MediaType { get; init; } = MediaTypeValue;

    public required long Bytes { get; init; }

    public required string Sha256 { get; init; }

    public required string CaptureId { get; init; }

    public required IReadOnlyList<string> Fields { get; init; }

    public required string VectorLayer { get; init; }

    public required int Minzoom { get; init; }

    public required int Maxzoom { get; init; }
}

public sealed record GeoparquetExportArtifact
{
    public const string MediaTypeValue = "application/vnd.apache.parquet";

    public required string Href { get; init; }

    public string MediaType { get; init; } = MediaTypeValue;

    public required long Bytes { get; init; }

    public required string Sha256 { get; init; }

    public required string CaptureId { get; init; }

    public required IReadOnlyList<string> Fields { get; init; }
}

public sealed record ExportValidation(string Status, DateTimeOffset ValidatedAt, IReadOnlyList<ExportValidationCheck> Checks)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";
}

public sealed record ExportValidationCheck(string Name, string Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
}

public sealed record PublishedExportVersion(string ExportId, string Version, string CaptureId, string CollectionKey, string PointerKey);

public sealed record ExportPointer(string ExportId, string Version, string Collection, DateTimeOffset SwitchedAt);

public sealed record ExportUnpublishedMarker(string ExportId, string ServiceId, string LayerId, DateTimeOffset UnpublishedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ExportVersion))]
[JsonSerializable(typeof(ExportPointer))]
[JsonSerializable(typeof(ExportUnpublishedMarker))]
[JsonSerializable(typeof(JsonObject))]
public sealed partial class ExportVersionJsonContext : JsonSerializerContext;
