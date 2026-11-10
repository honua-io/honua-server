// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public static class OgcCoverageMigrationCompatibilityCodes
{
    public const string GeoTiffSupported = "OGC_COVERAGE_GEOTIFF_SUPPORTED";

    public const string CogSupported = "OGC_COVERAGE_COG_SUPPORTED";

    public const string ManualReview = "OGC_COVERAGE_MANUAL_REVIEW";

    public const string OutputFormatMissing = "OGC_COVERAGE_OUTPUT_FORMAT_MISSING";

    public const string NetCdfUnsupported = "OGC_COVERAGE_NETCDF_UNSUPPORTED";

    public const string HdfUnsupported = "OGC_COVERAGE_HDF_UNSUPPORTED";

    public const string ZarrUnsupported = "OGC_COVERAGE_ZARR_UNSUPPORTED";

    public const string ScientificFormatUnsupported = "OGC_COVERAGE_SCIENTIFIC_FORMAT_UNSUPPORTED";

    public const string UnsupportedFormat = "OGC_COVERAGE_UNSUPPORTED_FORMAT";
}
