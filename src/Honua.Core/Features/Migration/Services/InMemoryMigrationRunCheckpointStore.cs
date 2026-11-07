// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

public sealed class InMemoryMigrationRunCheckpointStore : IMigrationRunCheckpointStore
{
    private readonly ConcurrentDictionary<string, MigrationRunCheckpoint> _checkpoints =
        new(StringComparer.Ordinal);

    public int Count => _checkpoints.Count;

    public ValueTask SaveAsync(MigrationRunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = MigrationRunCheckpointSanitizer.Sanitize(checkpoint);
        _checkpoints[sanitized.RunId] = sanitized;
        return ValueTask.CompletedTask;
    }

    public ValueTask<MigrationRunCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        return _checkpoints.TryGetValue(runId, out var checkpoint)
            ? ValueTask.FromResult<MigrationRunCheckpoint?>(checkpoint)
            : ValueTask.FromResult<MigrationRunCheckpoint?>(null);
    }

    public ValueTask<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_checkpoints.TryRemove(runId, out _));
    }
}
