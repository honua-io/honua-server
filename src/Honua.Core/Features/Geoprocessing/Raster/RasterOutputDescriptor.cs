// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>
/// Version constants for the provider-neutral raster output contract (#3089).
/// </summary>
public static class RasterOutputContract
{
    /// <summary>The earliest output descriptor version understood by this release.</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>The output descriptor version written by this release.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Absolute decoded-byte ceiling for an inline raster output carried by the JSON
    /// contract. Deployments may configure a lower publication limit, but deserialization
    /// never materializes a payload above this boundary.
    /// </summary>
    public const int MaximumInlinePayloadBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Producing-engine identity for outputs produced by the isolated GDAL worker —
    /// the single ordinary raster analysis engine per ADR-0071.
    /// </summary>
    public const string GdalWorkerEngine = "gdal-worker";
}

/// <summary>
/// Identifies a raster output produced by a geoprocessing job attempt without
/// materializing its content in the durable job record, Redis, or the web heap.
/// </summary>
/// <remarks>
/// Descriptors are published by the executing worker as the durable artifact
/// reference. They carry only stable logical identities plus content-integrity
/// metadata; never raw credentials, presigned/expiring URLs, or provider SDK
/// state. The attempt identity fences retried publication: a stale attempt's
/// descriptor can never displace the winning attempt's output (#3089, ADR-0071).
/// There is deliberately no Zarr output kind — a native Zarr result is a
/// multi-object hierarchy whose publication protocol is owned by #3103 and is
/// rejected fail-closed by <see cref="RasterOutputDescriptorValidator"/>.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "outputType")]
[JsonDerivedType(typeof(StagedObjectRasterOutputDescriptor), "staged-object")]
[JsonDerivedType(typeof(PostgisRasterOutputDescriptor), "postgis")]
[JsonDerivedType(typeof(InlineRasterOutputDescriptor), "inline")]
public abstract record RasterOutputDescriptor
{
    /// <summary>The version of this descriptor schema.</summary>
    [JsonRequired]
    public int OutputContractVersion { get; init; } = RasterOutputContract.CurrentVersion;

    /// <summary>Durable execution job that produced this output.</summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Execution attempt that produced this output. Attempt numbers are assigned by
    /// the queue claim and increase monotonically, so this value both scopes the
    /// staged object key and acts as the publication fence identity.
    /// </summary>
    public required int AttemptNumber { get; init; }

    /// <summary>Stable logical output name within the producing process.</summary>
    public required string OutputName { get; init; }

    /// <summary>Content identity and integrity metadata (size, media type, checksum).</summary>
    public required RasterContentIdentity Content { get; init; }

    /// <summary>Grid summary (dimensions, pixel scale, CRS) when the producer can bound it.</summary>
    public RasterOutputGridSummary? Grid { get; init; }

    /// <summary>Engine that produced the output, e.g. <see cref="RasterOutputContract.GdalWorkerEngine"/>.</summary>
    public required string ProducingEngine { get; init; }

    /// <summary>Provenance lineage for the output.</summary>
    public RasterOutputLineage? Lineage { get; init; }
}

/// <summary>
/// References an immutable single-object artifact staged by the executing attempt in a
/// registered geoprocessing output object store.
/// </summary>
public sealed record StagedObjectRasterOutputDescriptor : RasterOutputDescriptor
{
    /// <summary>Storage provider whose execution-owned credentials open the object.</summary>
    public required CloudStorageProvider Provider { get; init; }

    /// <summary>
    /// Logical identifier of the operator-registered output store. It is not a provider
    /// connection string, URI, or credential.
    /// </summary>
    public required string StoreReference { get; init; }

    /// <summary>Attempt-scoped immutable object key within the registered store.</summary>
    public required string ObjectKey { get; init; }
}

/// <summary>
/// References a raster registered into the PostGIS raster store as the output sink.
/// </summary>
public sealed record PostgisRasterOutputDescriptor : RasterOutputDescriptor
{
    /// <summary>Tenant-scoped catalog layer identifier the raster registered into.</summary>
    public required int LayerId { get; init; }

    /// <summary>Tenant-scoped raster identifier within the layer.</summary>
    public required long RasterId { get; init; }
}

/// <summary>
/// Carries a deliberately small raster output inline for protocol compatibility.
/// Publication validation enforces the configured byte ceiling; the inline path is
/// never an automatic fallback for an output the worker failed to stage.
/// </summary>
public sealed record InlineRasterOutputDescriptor : RasterOutputDescriptor
{
    /// <summary>Small inline output payload. JSON encodes this byte array as base64.</summary>
    [JsonConverter(typeof(BoundedInlineRasterOutputPayloadJsonConverter))]
    public required byte[] Payload { get; init; }
}

/// <summary>Bounded output grid summary recorded on published raster outputs.</summary>
public sealed record RasterOutputGridSummary
{
    /// <summary>Output width in pixels.</summary>
    public required long Width { get; init; }

    /// <summary>Output height in pixels.</summary>
    public required long Height { get; init; }

    /// <summary>Output band count.</summary>
    public required int BandCount { get; init; }

    /// <summary>Largest sample width across bands, in bits.</summary>
    public required int BitsPerSample { get; init; }

    /// <summary>Ground-unit pixel size when the encoded output declares one.</summary>
    public RasterSourcePixelScale? PixelScale { get; init; }

    /// <summary>
    /// Coordinate reference system identifier when the producing process pinned one
    /// (for example the reprojection target CRS). Null when the producer could not
    /// determine the CRS from a bounded header probe.
    /// </summary>
    public string? CoordinateReferenceSystem { get; init; }
}

/// <summary>Provenance lineage recorded on published raster outputs.</summary>
public sealed record RasterOutputLineage
{
    /// <summary>Catalog process identifier that produced the output.</summary>
    public string? ProcessId { get; init; }

    /// <summary>Analysis plan identifier the job executed.</summary>
    public string? PlanId { get; init; }

    /// <summary>
    /// Stable references of the raster sources consumed by the producing step
    /// (descriptor versions or catalog identities, never payload or credentials).
    /// </summary>
    public IReadOnlyList<string> SourceReferences { get; init; } = Array.Empty<string>();
}
