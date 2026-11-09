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

// Payloads stay JSONB so the store is not coupled to artifact schema versions; kind and version live in the document.
public interface IGeoServicesMigrationEvidenceStore
{
    Task SaveManifestAsync(
        GeoServicesMigrationRunRecord record,
        MigrationManifestArtifact manifest,
        CancellationToken cancellationToken = default);

    // The matching manifest record must already exist; otherwise the call throws.
    Task SaveParityAsync(
        string runId,
        GeoServicesMigrationParityArtifact parity,
        CancellationToken cancellationToken = default);

    Task<GeoServicesMigrationRunListResult> ListAsync(
        GeoServicesMigrationRunFilter filter,
        CancellationToken cancellationToken = default);

    Task<MigrationManifestArtifact?> GetManifestAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<GeoServicesMigrationParityArtifact?> GetParityAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
