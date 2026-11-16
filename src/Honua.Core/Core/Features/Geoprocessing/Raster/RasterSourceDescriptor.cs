// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster;

public static class RasterSourceContract
{
    public const int MinimumSupportedVersion = 1;

    public const int CurrentVersion = 1;

    public const int JobContractVersion = 2;

    public const int MaximumInlinePayloadBytes = 64 * 1024;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "sourceType")]
[JsonDerivedType(typeof(PostgisRasterSourceDescriptor), "postgis")]
[JsonDerivedType(typeof(ObjectStoreCogRasterSourceDescriptor), "cog")]
[JsonDerivedType(typeof(ObjectStoreZarrRasterSourceDescriptor), "zarr")]
[JsonDerivedType(typeof(StagedArtifactRasterSourceDescriptor), "staged-artifact")]
[JsonDerivedType(typeof(InlineRasterSourceDescriptor), "inline")]
public abstract record RasterSourceDescriptor
{
    [JsonRequired]
    public int SourceContractVersion { get; init; } = RasterSourceContract.CurrentVersion;

    public required string Version { get; init; }

    public required RasterContentIdentity Content { get; init; }

    public required RasterSecurityContextReference SecurityContext { get; init; }

    public RasterSourceSelection? Selection { get; init; }
}

public sealed record PostgisRasterSourceDescriptor : RasterSourceDescriptor
{
    public required int LayerId { get; init; }

    public required long RasterId { get; init; }
}

public sealed record ObjectStoreCogRasterSourceDescriptor : RasterSourceDescriptor
{
    public required CloudStorageProvider Provider { get; init; }

    public int? CatalogLayerId { get; init; }

    public long? CatalogRasterId { get; init; }

    public required string StoreReference { get; init; }

    public required string ObjectKey { get; init; }

    public RasterSourceDimensions? DeclaredDimensions { get; init; }

    public RasterSourcePixelScale? DeclaredPixelScale { get; init; }
}

public sealed record RasterSourceDimensions(
    long Width,
    long Height,
    int BandCount,
    int BitsPerSample);

public sealed record RasterSourcePixelScale(double X, double Y);

public sealed record ObjectStoreZarrRasterSourceDescriptor : RasterSourceDescriptor
{
    public required string StoreReference { get; init; }

    public required string ObjectKey { get; init; }

    public required string ArrayPath { get; init; }
}

public sealed record StagedArtifactRasterSourceDescriptor : RasterSourceDescriptor
{
    public required string ArtifactReference { get; init; }

    public CloudStorageProvider? Provider { get; init; }

    public string? StoreReference { get; init; }

    public string? ObjectKey { get; init; }

    public RasterSourceDimensions? DeclaredDimensions { get; init; }

    public RasterSourcePixelScale? DeclaredPixelScale { get; init; }
}

public sealed record InlineRasterSourceDescriptor : RasterSourceDescriptor
{
    [JsonConverter(typeof(BoundedInlineRasterPayloadJsonConverter))]
    public required byte[] Payload { get; init; }
}

public sealed record RasterContentIdentity
{
    public required long SizeBytes { get; init; }

    public required string MediaType { get; init; }

    public RasterChecksum? Checksum { get; init; }

    public string? ETag { get; init; }
}

public sealed record RasterChecksum(string Algorithm, string Value);

public sealed record RasterSecurityContextReference
{
    public required string TenantId { get; init; }

    public required string AuthorizationSnapshotReference { get; init; }

    [JsonIgnore]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The instance property makes the untrusted status explicit on every caller-authored reference.")]
    public bool IsTrusted => false;
}

public sealed record RasterSourceSelection
{
    public RasterPixelWindow? PixelWindow { get; init; }

    public IReadOnlyList<int> Bands { get; init; } = Array.Empty<int>();

    public RasterTimeSelection? Time { get; init; }

    public IReadOnlyList<RasterDimensionSlice> Dimensions { get; init; } = Array.Empty<RasterDimensionSlice>();
}

public sealed record RasterPixelWindow(long X, long Y, long Width, long Height);

public sealed record RasterTimeSelection(DateTimeOffset Start, DateTimeOffset End);

public sealed record RasterDimensionSlice(string Dimension, long Start, long Stop, long Step = 1);
