// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// Guardrails applied by <see cref="CatalogRasterSourceResolver"/> when materializing a
/// registered catalog raster onto a geoprocessing plan's inline <c>source</c> input (#2264).
/// </summary>
internal sealed class CatalogRasterSourceOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Cog:RasterSource";

    /// <summary>
    /// Maximum projected <em>decoded</em> raster size, in bytes, accepted for inline sourcing.
    /// The existing artifact ceiling bounds only the compressed bytes read from the object; this
    /// bounds the grid the worker would allocate when it decodes them (<c>width * height * bands *
    /// ceil(bits/8)</c>), closing the COG decompression-bomb path (#3090). Sources whose declared
    /// header exceeds this are rejected at submit before any bytes are materialized. Default 2 GiB.
    /// </summary>
    [Range(1024L * 1024L, 64L * 1024L * 1024L * 1024L,
        ErrorMessage = "MaxDecodedRasterBytes must be between 1 MiB and 64 GiB")]
    public long MaxDecodedRasterBytes { get; set; } = 2L * 1024L * 1024L * 1024L;
}
