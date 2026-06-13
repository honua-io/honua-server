// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Captures the state needed for the background database-recovery loop when the host starts in
/// degraded mode because the database was unavailable at boot. Populated by the startup path and
/// consumed by <see cref="DatabaseRecoveryBackgroundService"/>.
/// </summary>
internal sealed class DegradedStartupContext
{
    private int _degraded;

    /// <summary>
    /// Whether the host entered degraded mode (database unavailable at startup).
    /// </summary>
    public bool IsDegraded => Volatile.Read(ref _degraded) == 1;

    /// <summary>
    /// Whether the degraded start was caused by a migration failure (vs. preflight only). When
    /// <see langword="true"/>, the recovery loop reruns migrations after connectivity is restored.
    /// </summary>
    public bool MigrationsPending { get; private set; }

    /// <summary>
    /// The assembly carrying embedded migration scripts, captured at startup so the recovery loop
    /// can rerun migrations without re-resolving the entry assembly.
    /// </summary>
    public Assembly? MigrationsAssembly { get; private set; }

    /// <summary>
    /// Marks the host as degraded and records the recovery inputs.
    /// </summary>
    /// <param name="migrationsPending">Whether migrations still need to run after recovery.</param>
    /// <param name="migrationsAssembly">The assembly carrying migration scripts.</param>
    public void MarkDegraded(bool migrationsPending, Assembly? migrationsAssembly)
    {
        MigrationsPending = migrationsPending;
        MigrationsAssembly = migrationsAssembly;
        Volatile.Write(ref _degraded, 1);
    }
}
