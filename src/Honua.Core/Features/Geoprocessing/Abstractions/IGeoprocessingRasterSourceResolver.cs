// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.Geoprocessing.Abstractions;

/// <summary>
/// Resolves a geoprocessing raster-source reference (<c>layerId</c> / <c>rasterId</c>)
/// to immutable, metadata-only raster descriptors so native raster/surface workers can
/// open registered catalog rasters directly without a manual inline upload (#2264/#3090).
///
/// <para>
/// The resolver runs on the SUBMIT side (the serving image, where the raster
/// catalog and cloud range readers are available), NOT inside the GDAL worker.
/// It performs catalog lookup and provider HEAD/properties requests only; object bytes
/// remain in object storage and the selected worker opens the pinned descriptor. Deployments
/// without a raster catalog leave this unregistered; the submit path then rejects
/// layer-referenced plans with a clear "not available in this deployment" message
/// rather than failing later in the worker.
/// </para>
/// </summary>
public interface IGeoprocessingRasterSourceResolver
{
    /// <summary>
    /// Resolves a registered raster to its owning catalog layer without reading raster bytes.
    /// A missing registration or a <see cref="RasterSourceReference.LayerId"/> hint that does
    /// not match the registration returns <see cref="RasterSourceLayerResolution.NotFound"/>
    /// so the submit pipeline can fail through the same non-enumerating authorization channel.
    /// </summary>
    Task<RasterSourceLayerResolution> ResolveLayerIdAsync(
        RasterSourceReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="reference"/> to a metadata-only immutable descriptor.
    /// Returns a failure result (never throws) when the reference cannot be resolved so
    /// the caller maps it onto the submit-time validation channel.
    /// </summary>
    Task<RasterSourceResolution> ResolveAsync(
        RasterSourceReference reference,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A reference to a registered catalog raster. Exactly one of <see cref="RasterId"/>
/// or <see cref="LayerId"/> drives resolution; when both are supplied the
/// <see cref="RasterId"/> is authoritative and the <see cref="LayerId"/> is treated
/// as a consistency hint.
/// </summary>
/// <param name="LayerId">Catalog layer identifier whose registered raster to resolve.</param>
/// <param name="RasterId">Registered raster identifier to resolve directly.</param>
public sealed record RasterSourceReference(int? LayerId, long? RasterId);

/// <summary>
/// Metadata-only outcome for resolving a raster registration to its owning catalog layer.
/// Deliberately carries no missing-resource explanation so unknown, mismatched, and forbidden
/// references remain indistinguishable to callers.
/// </summary>
public sealed record RasterSourceLayerResolution
{
    /// <summary>Whether the registration and any supplied layer hint matched.</summary>
    public bool Found { get; init; }

    /// <summary>The owning catalog layer when <see cref="Found"/> is <see langword="true"/>.</summary>
    public int? LayerId { get; init; }

    /// <summary>Builds a successful metadata resolution.</summary>
    public static RasterSourceLayerResolution Success(int layerId) =>
        new() { Found = true, LayerId = layerId };

    /// <summary>Builds a non-enumerating missing or mismatched result.</summary>
    public static RasterSourceLayerResolution NotFound() => new();
}

/// <summary>
/// Outcome of a <see cref="IGeoprocessingRasterSourceResolver.ResolveAsync"/> call.
/// </summary>
public sealed record RasterSourceResolution
{
    /// <summary>Whether the reference resolved to an immutable descriptor.</summary>
    public bool Found { get; init; }

    /// <summary>The resolved descriptor when <see cref="Found"/> is <c>true</c>.</summary>
    public RasterSourceDescriptor? Descriptor { get; init; }

    /// <summary>
    /// A caller-safe explanation when <see cref="Found"/> is <c>false</c>. Must not
    /// leak provider internals, paths, or connection details.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>Builds a successful resolution carrying <paramref name="descriptor"/>.</summary>
    public static RasterSourceResolution Success(RasterSourceDescriptor descriptor) =>
        new() { Found = true, Descriptor = descriptor };

    /// <summary>Builds a failed resolution carrying a caller-safe <paramref name="reason"/>.</summary>
    public static RasterSourceResolution Failure(string reason) =>
        new() { Found = false, FailureReason = reason };
}
