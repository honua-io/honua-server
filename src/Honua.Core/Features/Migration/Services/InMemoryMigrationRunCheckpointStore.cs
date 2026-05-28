// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// In-memory implementation of <see cref="IMigrationRunCheckpointStore"/>. Intended for
/// tests and for short-lived in-process migration runs that do not need to survive a
/// process restart. Persisted scenarios should use
/// <see cref="FileSystemMigrationRunCheckpointStore"/> or another backing store.
/// </summary>
public sealed class InMemoryMigrationRunCheckpointStore : IMigrationRunCheckpointStore
{
    private readonly ConcurrentDictionary<string, MigrationRunCheckpoint> _checkpoints =
        new(StringComparer.Ordinal);

    /// <summary>Number of checkpoints currently persisted. Exposed for tests.</summary>
    public int Count => _checkpoints.Count;

    /// <inheritdoc />
    public ValueTask SaveAsync(MigrationRunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = MigrationRunCheckpointSanitizer.Sanitize(checkpoint);
        _checkpoints[sanitized.RunId] = sanitized;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<MigrationRunCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        return _checkpoints.TryGetValue(runId, out var checkpoint)
            ? ValueTask.FromResult<MigrationRunCheckpoint?>(checkpoint)
            : ValueTask.FromResult<MigrationRunCheckpoint?>(null);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_checkpoints.TryRemove(runId, out _));
    }
}
