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

// Markers that look like URLs are rejected, and marker payloads are truncated to a fixed bound.
public interface IMigrationRunCheckpointStore
{
    ValueTask SaveAsync(MigrationRunCheckpoint checkpoint, CancellationToken cancellationToken = default);

    ValueTask<MigrationRunCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default);
}
