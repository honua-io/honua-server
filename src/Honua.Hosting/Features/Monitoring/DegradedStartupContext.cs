// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Captures the state needed for the background database-recovery loop when the host starts in
/// degraded mode because the database was unavailable at boot. Populated by the startup path and
/// consumed by <see cref="DatabaseRecoveryBackgroundService"/> and the readiness probe.
/// </summary>
internal sealed class DegradedStartupContext
{
    private int _degraded;
    private int _recovered;
    private volatile string? _reason;
    private volatile string? _recoveryFailure;

    /// <summary>
    /// Whether the host entered degraded mode (database unavailable at startup).
    /// </summary>
    public bool IsDegraded => Volatile.Read(ref _degraded) == 1;

    /// <summary>
    /// Whether the background recovery loop restored database access after a degraded start.
    /// </summary>
    public bool IsRecovered => Volatile.Read(ref _recovered) == 1;

    /// <summary>
    /// Whether the host is still serving without database access because of a degraded start.
    /// Readiness reports not-ready while this holds.
    /// </summary>
    public bool IsAwaitingDatabase => IsDegraded && !IsRecovered;

    /// <summary>
    /// Sanitized reason the host started degraded, such as
    /// <c>PostGIS preflight: SQLSTATE 53300 (class 53: insufficient resources)</c>. It names the
    /// startup phase and the failure class only, never a connection string or provider message.
    /// </summary>
    public string? Reason => _reason;

    /// <summary>
    /// Sanitized reason the recovery loop stopped retrying, or <see langword="null"/> while it is
    /// still retrying or after it recovered.
    /// </summary>
    public string? RecoveryFailure => _recoveryFailure;

    /// <summary>
    /// Whether the degraded start was caused by a migration failure (vs. preflight only). When
    /// <see langword="true"/>, the recovery loop reruns migrations after connectivity is restored.
    /// </summary>
    public bool MigrationsPending { get; private set; }

    /// <summary>
    /// Whether migrations are skipped by configuration and the journal/schema guard still has to
    /// verify the database after recovery. Degraded start defers that guard; it never bypasses it.
    /// </summary>
    public bool SchemaGuardPending { get; private set; }

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
    /// <param name="reason">Sanitized phase and failure class for readiness.</param>
    /// <param name="schemaGuardPending">Whether the schema guard must run after recovery instead of migrations.</param>
    public void MarkDegraded(
        bool migrationsPending,
        Assembly? migrationsAssembly,
        string? reason = null,
        bool schemaGuardPending = false)
    {
        MigrationsPending = migrationsPending;
        SchemaGuardPending = schemaGuardPending;
        MigrationsAssembly = migrationsAssembly;
        _reason = reason;
        Volatile.Write(ref _degraded, 1);
    }

    /// <summary>Records that the recovery loop restored database access.</summary>
    public void MarkRecovered()
    {
        _recoveryFailure = null;
        Volatile.Write(ref _recovered, 1);
    }

    /// <summary>Records that the recovery loop stopped retrying.</summary>
    /// <param name="failure">Sanitized reason recovery stopped.</param>
    public void MarkRecoveryAbandoned(string failure) => _recoveryFailure = failure;
}
