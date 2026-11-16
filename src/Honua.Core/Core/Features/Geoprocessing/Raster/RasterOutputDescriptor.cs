// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster;

public static class RasterOutputContract
{
    public const int MinimumSupportedVersion = 1;

    public const int CurrentVersion = 1;

    public const int MaximumInlinePayloadBytes = 8 * 1024 * 1024;

    public const string GdalWorkerEngine = "gdal-worker";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "outputType")]
[JsonDerivedType(typeof(StagedObjectRasterOutputDescriptor), "staged-object")]
[JsonDerivedType(typeof(PostgisRasterOutputDescriptor), "postgis")]
[JsonDerivedType(typeof(InlineRasterOutputDescriptor), "inline")]
public abstract record RasterOutputDescriptor
{
    [JsonRequired]
    public int OutputContractVersion { get; init; } = RasterOutputContract.CurrentVersion;

    public required string JobId { get; init; }

    public required int AttemptNumber { get; init; }

    public required string OutputName { get; init; }

    public required RasterContentIdentity Content { get; init; }

    public RasterOutputGridSummary? Grid { get; init; }

    public required string ProducingEngine { get; init; }

    public RasterOutputLineage? Lineage { get; init; }
}

public sealed record StagedObjectRasterOutputDescriptor : RasterOutputDescriptor
{
    public required CloudStorageProvider Provider { get; init; }

    public required string StoreReference { get; init; }

    public required string ObjectKey { get; init; }
}

public sealed record PostgisRasterOutputDescriptor : RasterOutputDescriptor
{
    public required int LayerId { get; init; }

    public required long RasterId { get; init; }
}

public sealed record InlineRasterOutputDescriptor : RasterOutputDescriptor
{
    [JsonConverter(typeof(BoundedInlineRasterOutputPayloadJsonConverter))]
    public required byte[] Payload { get; init; }
}

public sealed record RasterOutputGridSummary
{
    public required long Width { get; init; }

    public required long Height { get; init; }

    public required int BandCount { get; init; }

    public required int BitsPerSample { get; init; }

    public RasterSourcePixelScale? PixelScale { get; init; }

    public string? CoordinateReferenceSystem { get; init; }
}

public sealed record RasterOutputLineage
{
    public string? ProcessId { get; init; }

    public string? PlanId { get; init; }

    public IReadOnlyList<string> SourceReferences { get; init; } = Array.Empty<string>();
}
