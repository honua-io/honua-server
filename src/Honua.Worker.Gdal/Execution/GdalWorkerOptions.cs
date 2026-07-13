// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Configuration guardrails for the heavyweight GDAL worker executors.
/// </summary>
internal sealed class GdalWorkerOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "GdalWorker";

    /// <summary>
    /// Maximum size, in bytes, of a single source payload accepted from the
    /// durable spec, and of the produced artifact published back. Mirrors the
    /// lean executor 50 MiB default so the two profiles share one ceiling.
    /// </summary>
    [Range(1024, 1024L * 1024L * 1024L, ErrorMessage = "MaxArtifactBytes must be between 1 KiB and 1 GiB")]
    public long MaxArtifactBytes { get; set; } = 50L * 1024L * 1024L;

    /// <summary>
    /// Root directory under which per-job scratch workspaces are created. Each
    /// job gets an isolated subdirectory that is deleted after execution.
    /// Defaults to the OS temp directory.
    /// </summary>
    public string ScratchRoot { get; set; } = Path.Combine(Path.GetTempPath(), "honua-gdal-worker");

    /// <summary>
    /// Maximum wall-clock duration for a single GDAL tool invocation before the
    /// child process is killed and the job fails.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:05", "06:00:00", ErrorMessage = "ToolTimeout must be between 5 seconds and 6 hours")]
    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum DECLARED width, in pixels, of an inbound raster. Bounds a single
    /// runaway dimension even when the total pixel product would otherwise pass.
    /// Default 100 000 comfortably covers any legitimate scene/mosaic tile while
    /// rejecting a header that declares an absurd width (#2766).
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxRasterWidth must be a positive pixel count")]
    public int MaxRasterWidth { get; set; } = 100_000;

    /// <summary>
    /// Maximum DECLARED height, in pixels, of an inbound raster. Companion to
    /// <see cref="MaxRasterWidth"/>. Default 100 000 (#2766).
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxRasterHeight must be a positive pixel count")]
    public int MaxRasterHeight { get; set; } = 100_000;

    /// <summary>
    /// Maximum DECLARED total cell count (width × height) of an inbound raster.
    /// This is the primary decompression-bomb bound: a few-KB compressible
    /// GeoTIFF can declare enormous dimensions and force GDAL to allocate
    /// width×height×bands×dtype. Default 500 megapixels fits a full Sentinel-2
    /// tile (~120 MP) or a moderate mosaic, yet rejects a header declaring
    /// 100 000×100 000 = 10 000 MP (#2766).
    /// </summary>
    [Range(1L, long.MaxValue, ErrorMessage = "MaxRasterPixels must be a positive cell count")]
    public long MaxRasterPixels { get; set; } = 500_000_000L;

    /// <summary>
    /// Maximum DECLARED band count of an inbound raster. Legitimate hyperspectral
    /// imagery tops out near ~250 bands; the 512 default leaves headroom while
    /// rejecting a header declaring thousands of bands (#2766).
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxRasterBands must be a positive band count")]
    public int MaxRasterBands { get; set; } = 512;

    /// <summary>
    /// Maximum ESTIMATED fully-decoded raster footprint
    /// (width × height × bands × bytes-per-sample) admitted before a GDAL tool
    /// opens the raster. This is a CONSERVATIVE FLOOR on the allocation, not the
    /// true peak: real GDAL RSS also includes warp/block-cache buffers, dtype
    /// promotion, and palette/RGBA expansion, so actual usage can exceed this — it
    /// is a lower-bound admission gate, deliberately restrictive. The estimate uses
    /// the first band's bit depth. Default 4 GiB bounds the per-job worker
    /// allocation while still admitting e.g. a 500 MP × 4-band Byte raster
    /// (~2 GiB) (#2766).
    /// </summary>
    [Range(1024L, long.MaxValue, ErrorMessage = "MaxDecodedRasterBytes must be at least 1 KiB")]
    public long MaxDecodedRasterBytes { get; set; } = 4L * 1024L * 1024L * 1024L;

    /// <summary>
    /// Positive allowlist of raster INPUT container formats an untrusted source is
    /// permitted to be opened as (#2784). It MUST stay coordinated with the set
    /// <see cref="GdalRasterDimensionGuard"/> can dimension-check: only these formats
    /// are both openable AND bounded against the decompression-bomb caps
    /// (<see cref="MaxRasterWidth"/> / <see cref="MaxRasterHeight"/> /
    /// <see cref="MaxRasterPixels"/> / <see cref="MaxDecodedRasterBytes"/>).
    ///
    /// <para>
    /// The default set — TIFF (incl. BigTIFF / COG), PNG, JPEG — is exactly the set the
    /// guard reads dimensions from. A payload whose magic identifies a KNOWN raster
    /// container OUTSIDE this list (JPEG2000, GIF, BMP, NITF, HFA — each can declare an
    /// enormous canvas the guard cannot bound) is refused BEFORE a GDAL tool opens it.
    /// Unknown / non-raster magic is left to GDAL and the vector paths as before.
    /// </para>
    ///
    /// <para>
    /// <b>Extending this list is an explicit opt-in that reopens the OOM risk</b>: a
    /// format added here is admitted but NOT dimension-bounded, so a huge-canvas input
    /// in that format can force GDAL to allocate an oversized buffer. To actually open a
    /// new format, an operator must ALSO remove its driver from
    /// <see cref="GdalHardeningOptions.SkipDrivers"/> (the defense-in-depth
    /// <c>GDAL_SKIP</c> denial); the two knobs are kept closed together by default.
    /// Matching is case-insensitive; recognized keys: <c>TIFF</c>, <c>PNG</c>,
    /// <c>JPEG</c>, <c>JPEG2000</c>, <c>GIF</c>, <c>BMP</c>, <c>NITF</c>, <c>HFA</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Configuring this list REPLACES the default set</b> (it does not append): supplying
    /// <c>GdalWorker:AllowedRasterInputFormats = [ "TIFF" ]</c> yields an effective allowlist
    /// of exactly <c>TIFF</c> — PNG and JPEG are then refused. Leaving it unset keeps the
    /// TIFF/PNG/JPEG default. This is enforced in the worker registration, which post-binds
    /// the configured values over ConfigurationBinder's append-onto-defaults behavior.
    /// </para>
    /// </summary>
    public IList<string> AllowedRasterInputFormats { get; set; } = new List<string>
    {
        "TIFF",
        "PNG",
        "JPEG",
    };

    /// <summary>
    /// Maximum number of zone features accepted in a <c>raster.zonal-statistics</c>
    /// payload. Each zone drives its own gdalwarp + gdalinfo subprocess pair, so an
    /// unbounded zone FeatureCollection is a cumulative-work DoS even under the
    /// per-invocation <see cref="ToolTimeout"/>. Default 10 000 covers realistic
    /// administrative-boundary sets (counties, watersheds) while bounding the total
    /// subprocess fan-out (#2766).
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxZoneCount must be a positive feature count")]
    public int MaxZoneCount { get; set; } = 10_000;

    /// <summary>
    /// Maximum cumulative vertex count across all zone geometries in a
    /// <c>raster.zonal-statistics</c> payload. Bounds the cutline-writing / geometry
    /// cost independently of zone count so a handful of pathologically dense
    /// polygons cannot exhaust the worker. Default 2 000 000 vertices (#2766).
    /// </summary>
    [Range(1L, long.MaxValue, ErrorMessage = "MaxZoneVertices must be a positive vertex count")]
    public long MaxZoneVertices { get; set; } = 2_000_000L;
}
