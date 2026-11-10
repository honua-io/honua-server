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

namespace Honua.Core.Features.Migration.Services;

public interface IMigrationPerformanceEvidenceStore
{
    ValueTask SaveAsync(MigrationPerformanceEvidenceRecord record, CancellationToken cancellationToken = default);

    ValueTask<MigrationPerformanceEvidenceRecord?> GetLatestPassingAsync(CancellationToken cancellationToken = default);

    ValueTask<MigrationPerformanceEvidenceRecord?> GetByIdAsync(
        string evidenceId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MigrationPerformanceEvidenceRecord>> GetHistoryAsync(
        string? sourceFamily,
        string? fixtureSize,
        int limit,
        CancellationToken cancellationToken = default);
}
