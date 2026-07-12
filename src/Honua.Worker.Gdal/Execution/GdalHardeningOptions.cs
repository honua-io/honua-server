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
    /// These are the indirection / network / archive drivers that can dereference an
    /// external file or URL from within an opened dataset — none of the raster or
    /// vector <em>data</em> formats the worker ingests (GTiff, PNG, JPEG, GeoJSON,
    /// GPKG, CSV, FlatGeobuf, ESRI Shapefile) or reads from trusted cloud storage
    /// (NetCDF, HDF5, Zarr, GRIB) appears here, so skipping them does not affect a
    /// legitimate op. <c>GDAL_SKIP</c> is honored by both the GDAL (raster) and OGR
    /// (vector) driver registrars.
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
