// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Stable compatibility codes emitted by the OGC coverage migration inventory scanner.
/// </summary>
public static class OgcCoverageMigrationCompatibilityCodes
{
    /// <summary>Coverage can be migrated through a GeoTIFF output path.</summary>
    public const string GeoTiffSupported = "OGC_COVERAGE_GEOTIFF_SUPPORTED";

    /// <summary>Coverage can be migrated through a Cloud Optimized GeoTIFF output path.</summary>
    public const string CogSupported = "OGC_COVERAGE_COG_SUPPORTED";

    /// <summary>Coverage is discoverable, but needs manual migration review.</summary>
    public const string ManualReview = "OGC_COVERAGE_MANUAL_REVIEW";

    /// <summary>Coverage did not advertise an output format that can be planned deterministically.</summary>
    public const string OutputFormatMissing = "OGC_COVERAGE_OUTPUT_FORMAT_MISSING";

    /// <summary>Coverage advertises NetCDF output, which is not yet supported by the automated path.</summary>
    public const string NetCdfUnsupported = "OGC_COVERAGE_NETCDF_UNSUPPORTED";

    /// <summary>Coverage advertises HDF output, which is not yet supported by the automated path.</summary>
    public const string HdfUnsupported = "OGC_COVERAGE_HDF_UNSUPPORTED";

    /// <summary>Coverage advertises Zarr output, which is not yet supported by the automated path.</summary>
    public const string ZarrUnsupported = "OGC_COVERAGE_ZARR_UNSUPPORTED";

    /// <summary>Coverage advertises one or more scientific formats without an automated import path.</summary>
    public const string ScientificFormatUnsupported = "OGC_COVERAGE_SCIENTIFIC_FORMAT_UNSUPPORTED";

    /// <summary>Coverage advertises only output formats that are not recognized by this scanner.</summary>
    public const string UnsupportedFormat = "OGC_COVERAGE_UNSUPPORTED_FORMAT";
}
