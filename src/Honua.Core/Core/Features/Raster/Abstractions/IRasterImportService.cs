// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IRasterImportService
{
    Task<RasterImportResult> ImportAsync(
        RasterImportRequest request,
        IProgress<RasterImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    SupportedRasterFormat? DetectFormat(string fileName);

    string[] GetSupportedExtensions();
}
