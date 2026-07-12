// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Configurable, restrictive-by-default GDAL runtime hardening applied to every
/// GDAL/OGR CLI subprocess the worker launches (#2765). The defaults exclude the
/// virtual / indirection drivers that let a base64-supplied dataset reach off the
/// local scratch workspace (VRT <c>&lt;SourceFilename&gt;</c>, WMS/WMTS/WCS/STAC
/// service descriptions) and disable the remote virtual-filesystem handlers for
/// invocations that operate purely on local scratch files.
/// </summary>
internal sealed class GdalHardeningOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "GdalWorker:Hardening";

    /// <summary>
    /// GDAL driver short names excluded via <c>GDAL_SKIP</c> on every invocation.
    /// Two categories are skipped:
    /// <list type="bullet">
    /// <item>the indirection / network / archive drivers that can dereference an
    /// external file or URL from within an opened dataset (#2765); and</item>
    /// <item>the KNOWN decompression-bomb-capable raster drivers that fall OUTSIDE the
    /// dimension-guarded input allowlist (JPEG2000 / GIF / BMP / HFA / NITF / ENVI / RMF)
    /// — a huge-canvas input in any of these bypasses
    /// <see cref="GdalRasterDimensionGuard"/> (which only bounds TIFF/PNG/JPEG) and lets
    /// GDAL allocate an oversized buffer → OOM (#2784). This is the defense-in-depth
    /// companion to the pre-spawn content allowlist
    /// (<see cref="GdalWorkerOptions.AllowedRasterInputFormats"/>): both default closed
    /// to the same TIFF/PNG/JPEG set.</item>
    /// </list>
    /// None of the raster or vector <em>data</em> formats the worker ingests (GTiff,
    /// COG, PNG, JPEG, GeoJSON, GPKG, CSV, FlatGeobuf, ESRI Shapefile) or reads from
    /// trusted cloud storage (NetCDF, HDF5, Zarr, GRIB) appears here, and every raster
    /// executor writes <c>-of GTiff</c>, so skipping these does not affect a legitimate
    /// op. To open one of the bomb-capable formats an operator must remove its driver
    /// here AND add the format to <see cref="GdalWorkerOptions.AllowedRasterInputFormats"/>,
    /// accepting the documented OOM risk. <c>GDAL_SKIP</c> is honored by both the GDAL
    /// (raster) and OGR (vector) driver registrars.
    ///
    /// <para>
    /// <b>Configuring this list REPLACES the default denials</b> (it does not append):
    /// supplying <c>GdalWorker:Hardening:SkipDrivers</c> yields an effective
    /// <c>GDAL_SKIP</c> of exactly the configured values, so an operator can genuinely
    /// REMOVE a default skip driver (e.g. drop <c>JP2OpenJPEG</c> to open JPEG 2000).
    /// Leaving it unset keeps the full default denial set. This is enforced in the worker
    /// registration, which post-binds the configured values over ConfigurationBinder's
    /// append-onto-defaults behavior.
    /// </para>
    /// </summary>
    public IList<string> SkipDrivers { get; set; } = new List<string>
    {
        // Raster indirection / aggregation.
        "VRT",
        "GTI",
        "DERIVED",
        // GDAL Streamed Algorithm (GDAL >= 3.10): a JSON blob serializing a
        // gdal_translate/gdalwarp command line that can open a plain LOCAL path — it is
        // neither XML nor a /vsi reference, so the content pre-check does not catch it;
        // skipping the driver is the control. Pure attack surface, used by no executor.
        "GDALG",
        // Meta Raster Format: a header can point at a remote <CachedSource> URL.
        "MRF",
        // OGC / web service descriptions (auto-fetch on open).
        "WMS",
        "WMTS",
        "WCS",
        "HTTP",
        "STACIT",
        "STACTA",
        // Vector indirection / web services.
        "OGR_VRT",
        "WFS",
        "OAPIF",
        "NGW",
        "PLMOSAIC",
        "EEDA",
        "EEDAI",
        // Decompression-bomb-capable raster drivers OUTSIDE the dimension-guarded
        // TIFF/PNG/JPEG allowlist (#2784). Each can declare an enormous canvas that
        // GdalRasterDimensionGuard cannot bound, so deny the driver outright.
        // JPEG 2000 (SIZ dimensions to 2^32 — the strongest live vector):
        "JP2OpenJPEG",
        "JP2ECW",
        "JP2KAK",
        "JP2MrSID",
        "JP2Lura",
        "JPEG2000",
        // GIF (65535²), BMP (int32 dims):
        "GIF",
        "BIGGIF",
        "BMP",
        // Other openable, un-guarded raster containers with large declarable canvases:
        "HFA",
        "NITF",
        "ENVI",
        "RMF",
    };

    /// <summary>
    /// When <see langword="true"/> (the default), invocations that operate purely on
    /// local scratch files (no <c>/vsi</c> path in the argument vector) additionally
    /// get the remote virtual-filesystem handlers neutralized: the <c>/vsicurl</c>
    /// family is gated to an unmatchable extension, HTTP retries are disabled, and
    /// directory pre-scan on open is suppressed. Invocations that legitimately pass a
    /// <c>/vsi</c> path (the trusted cloud-hosted multidimensional-coverage reader,
    /// whose paths come from the catalog — never from an untrusted blob) keep remote
    /// VSI enabled so those bucket-scoped range reads still work.
    /// </summary>
    public bool DisableRemoteVsiForLocalInputs { get; set; } = true;
}
