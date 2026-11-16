// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Descriptors;

public sealed record PublicationDescriptor
{
    public const string SchemaUri = "https://honua.io/schemas/publication-descriptor.v1.schema.json";
    public const string CurrentVersion = "1.0";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaUri;

    public string DescriptorVersion { get; init; } = CurrentVersion;

    public required string PublicationId { get; init; }

    public required string Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public required PublicationSourceLayer SourceLayer { get; init; }

    public required PublicationCapture Capture { get; init; }

    public required double[] Bounds { get; init; }

    public required PublicationCrs Crs { get; init; }

    public required IReadOnlyList<PublicationField> Fields { get; init; }

    public required PublicationIdentity Identity { get; init; }

    public required PublicationGeometry Geometry { get; init; }

    public required PublicationArtifacts Artifacts { get; init; }

    public required PublicationValidation Validation { get; init; }

    public required PublicationLinks Links { get; init; }
}

public sealed record PublicationSourceLayer(string Service, string Layer)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }
}

public sealed record PublicationCapture(string CaptureId, DateTimeOffset CapturedAt, string SourceRevision, long FeatureCount);

public sealed record PublicationCrs(string Source, string Geoparquet)
{
    public string Pmtiles { get; init; } = "EPSG:3857";
}

public sealed record PublicationField(string Name, string Type, bool Public)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}

public sealed record PublicationIdentity(string Field, string PmtilesProperty, string GeoparquetColumn)
{
    public string Type { get; init; } = "string";
}

public sealed record PublicationGeometry(string Column, string Encoding, IReadOnlyList<string> Types)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BboxColumn { get; init; }
}

public sealed record PublicationArtifacts(PmtilesPublicationArtifact Pmtiles, GeoparquetPublicationArtifact Geoparquet);

public sealed record PmtilesPublicationArtifact
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

public sealed record GeoparquetPublicationArtifact
{
    public const string MediaTypeValue = "application/vnd.apache.parquet";

    public required string Href { get; init; }

    public string MediaType { get; init; } = MediaTypeValue;

    public required long Bytes { get; init; }

    public required string Sha256 { get; init; }

    public required string CaptureId { get; init; }

    public required IReadOnlyList<string> Fields { get; init; }
}

public sealed record PublicationValidation(string Status, DateTimeOffset ValidatedAt, IReadOnlyList<PublicationValidationCheck> Checks)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";
}

public sealed record PublicationValidationCheck(string Name, string Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
}

public sealed record PublicationLinks(string Stac)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Self { get; init; }
}

public sealed record PublishedPublication(string PublicationId, string Version, string CaptureId, string DescriptorKey, string PointerKey);

public sealed record PublicationPointer(string PublicationId, string Version, string Descriptor, DateTimeOffset SwitchedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PublicationDescriptor))]
[JsonSerializable(typeof(PublicationPointer))]
public sealed partial class PublicationDescriptorJsonContext : JsonSerializerContext;
