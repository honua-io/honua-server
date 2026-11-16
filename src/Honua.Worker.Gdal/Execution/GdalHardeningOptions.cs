// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

internal sealed class GdalHardeningOptions
{
    public const string SectionName = "GdalWorker:Hardening";

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
        // TIFF/PNG/JPEG allowlist. Each can declare an enormous canvas that
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

    // On by default: local-scratch invocations get the remote virtual-filesystem
    // handlers neutralized (vsicurl gated to an unmatchable extension, no HTTP retries,
    // no directory pre-scan on open). Only invocations whose arguments include a path an
    // executor declared through GdalRemoteInputScope keep remote VSI, for
    // catalog-registered COG and multidimensional coverage reads, never an untrusted
    // blob or a job-supplied string.
    public bool DisableRemoteVsiForLocalInputs { get; set; } = true;
}
