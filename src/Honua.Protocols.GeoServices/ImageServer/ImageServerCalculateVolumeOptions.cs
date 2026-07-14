// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Admission limits for the ImageServer <c>calculateVolume</c> operation. These bound the CPU and
/// memory an unauthenticated volume request can consume so the synchronous operation is safe to
/// enable in a rolling deployment, mirroring the <c>computeClassStatistics</c> precedent (#2662,
/// ADR-0064). An AOI whose DEM clip exceeds the pixel budget is rejected with an actionable error
/// rather than analysed partially; the async-job path for unbounded AOIs is deferred (ADR-0064 §4).
/// </summary>
public sealed class ImageServerCalculateVolumeOptions
{
    /// <summary>The configuration section name that binds to these options.</summary>
    public const string SectionName = "GeoServices:ImageServer:CalculateVolume";

    /// <summary>
    /// Maximum number of DEM pixels (clip bounding box) integrated per area-of-interest before the
    /// request is rejected. Bounds the memory a single volume calculation can materialize. Defaults
    /// to 4,000,000 (a 2000x2000 AOI), matching the class-statistics per-class budget.
    /// </summary>
    [Range(1, 100_000_000)]
    public int MaxPixelsPerGeometry { get; set; } = 4_000_000;

    /// <summary>
    /// Maximum number of area-of-interest geometries accepted in one request. Bounds the total
    /// synchronous analysis work. Defaults to 32.
    /// </summary>
    [Range(1, 1024)]
    public int MaxGeometries { get; set; } = 32;
}
