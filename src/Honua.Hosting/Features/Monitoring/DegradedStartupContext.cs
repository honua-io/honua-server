// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;

namespace Honua.Infrastructure.Monitoring;

internal sealed class DegradedStartupContext
{
    private int _degraded;
    private int _recovered;
    private volatile string? _reason;
    private volatile string? _recoveryFailure;
    private volatile string? _reasonCode;
    private volatile Func<TimeSpan, CancellationToken, Task>? _readinessProbeRecovery;

    public bool IsDegraded => Volatile.Read(ref _degraded) == 1;

    public bool IsRecovered => Volatile.Read(ref _recovered) == 1;

    public bool IsAwaitingDatabase => IsDegraded && !IsRecovered;

    // The degraded reason names the phase and failure class only, never a connection string or provider message.
    public string? Reason => _reason;

    public string? ReasonCode => _reasonCode;

    public string? RecoveryFailure => _recoveryFailure;

    public bool MigrationsPending { get; private set; }

    // Skipping migrations does not skip the journal and schema guard.
    public bool SchemaGuardPending { get; private set; }

    public Assembly? MigrationsAssembly { get; private set; }

    public void MarkDegraded(
        bool migrationsPending,
        Assembly? migrationsAssembly,
        string? reason = null,
        bool schemaGuardPending = false,
        string? reasonCode = null)
    {
        MigrationsPending = migrationsPending;
        SchemaGuardPending = schemaGuardPending;
        MigrationsAssembly = migrationsAssembly;
        _reason = reason;
        _reasonCode = reasonCode;
        Volatile.Write(ref _degraded, 1);
    }

    public void UpdateReason(string reasonCode, string reason)
    {
        _reasonCode = reasonCode;
        _reason = reason;
    }

    public void MarkRecovered()
    {
        _recoveryFailure = null;
        Volatile.Write(ref _recovered, 1);
    }

    public void MarkRecoveryAbandoned(string failure) => _recoveryFailure = failure;

    // The probe attempt must not throw for a failed connect; a host that never registers one leaves the probe as a status report.
    internal void RegisterReadinessProbeRecovery(Func<TimeSpan, CancellationToken, Task> recover)
    {
        ArgumentNullException.ThrowIfNull(recover);
        _readinessProbeRecovery = recover;
    }

    internal async Task TryRecoverOnReadinessProbeAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var recover = _readinessProbeRecovery;
        if (recover is null || !IsAwaitingDatabase || RecoveryFailure is not null)
        {
            return;
        }

        await recover(budget, cancellationToken);
    }
}
