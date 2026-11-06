// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

// Only image/tiff is applied automatically; any other WCS format goes to manual review, and ingestion uses the single coverage sink.
public interface IOgcWcsImportService
{
    Task<OgcWcsImportResult> ImportAsync(
        OgcWcsImportRequest request,
        CancellationToken cancellationToken = default);
}
