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

public interface IGeoServerImportService
{
    Task<GeoServerServiceInfo> DiscoverServiceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationSourceInventoryArtifact> ScanSourceAsync(
        GeoServerDiscoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        CancellationToken cancellationToken = default);

    Task<GeoServerImportResult> ImportConfigurationAsync(
        GeoServerImportRequest request,
        IProgress<GeoServerImportProgress>? progress,
        CancellationToken cancellationToken = default);
}
