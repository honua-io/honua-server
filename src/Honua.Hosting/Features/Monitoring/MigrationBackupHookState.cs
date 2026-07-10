// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Process-local cache of the most recent migration backup-hook outcome.
/// </summary>
internal sealed class MigrationBackupHookState
{
    private DatabaseMigrationBackupHookResult? _latest;

    /// <summary>
    /// Latest backup-hook outcome observed by this instance.
    /// </summary>
    public DatabaseMigrationBackupHookResult? Latest => Volatile.Read(ref _latest);

    /// <summary>
    /// Stores the latest backup-hook outcome.
    /// </summary>
    /// <param name="result">Outcome to cache.</param>
    public void Record(DatabaseMigrationBackupHookResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Volatile.Write(ref _latest, result);
    }
}
