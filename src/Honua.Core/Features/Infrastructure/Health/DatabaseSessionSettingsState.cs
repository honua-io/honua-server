// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Core.Features.Infrastructure.Health;

/// <summary>
/// Startup verification status of the database session settings in
/// <see cref="DatabaseSessionSettingsMode.RoleDefaults"/> mode.
/// </summary>
public enum DatabaseSessionSettingsVerificationStatus
{
    /// <summary>Verification does not apply (<c>connection</c> mode sets the values itself).</summary>
    NotApplicable,

    /// <summary>Verification has not completed yet.</summary>
    Pending,

    /// <summary>The role defaults match the configured limits.</summary>
    Verified,

    /// <summary>One or more role defaults are unset or differ from the configured limits.</summary>
    Mismatch,

    /// <summary>The settings could not be read (for example, the database was unreachable).</summary>
    Unavailable,
}

/// <summary>
/// One session setting whose effective value differs from the configured value.
/// </summary>
/// <param name="Setting">The PostgreSQL setting name.</param>
/// <param name="Expected">The value the server would have set itself in <c>connection</c> mode.</param>
/// <param name="Actual">The effective value observed on a fresh connection, or null when absent.</param>
/// <param name="Remediation">The <c>ALTER ROLE</c> statement an operator runs to fix it.</param>
public sealed record DatabaseSessionSettingDrift(string Setting, string Expected, string? Actual, string Remediation);

/// <summary>
/// Snapshot of the session-settings mode and its startup verification.
/// </summary>
public sealed record DatabaseSessionSettingsSnapshot
{
    /// <summary>The configured mode (<c>connection</c> or <c>role-defaults</c>).</summary>
    public required DatabaseSessionSettingsMode Mode { get; init; }

    /// <summary>The verification status.</summary>
    public required DatabaseSessionSettingsVerificationStatus Status { get; init; }

    /// <summary>The database role the settings were read for, when known.</summary>
    public string? Role { get; init; }

    /// <summary>Settings that are unset or differ from the configured values.</summary>
    public IReadOnlyList<DatabaseSessionSettingDrift> Drift { get; init; } = Array.Empty<DatabaseSessionSettingDrift>();

    /// <summary>Why verification is unavailable, when it is.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Holds the session-settings mode and the latest startup verification result for diagnostics.
/// </summary>
public sealed class DatabaseSessionSettingsState
{
    private volatile DatabaseSessionSettingsSnapshot _snapshot;

    /// <summary>
    /// Creates the state for a mode. <c>connection</c> mode is
    /// <see cref="DatabaseSessionSettingsVerificationStatus.NotApplicable"/>; <c>role-defaults</c>
    /// starts <see cref="DatabaseSessionSettingsVerificationStatus.Pending"/>.
    /// </summary>
    /// <param name="mode">The configured mode.</param>
    public DatabaseSessionSettingsState(DatabaseSessionSettingsMode mode)
    {
        _snapshot = new DatabaseSessionSettingsSnapshot
        {
            Mode = mode,
            Status = mode == DatabaseSessionSettingsMode.RoleDefaults
                ? DatabaseSessionSettingsVerificationStatus.Pending
                : DatabaseSessionSettingsVerificationStatus.NotApplicable,
        };
    }

    /// <summary>The configured mode.</summary>
    public DatabaseSessionSettingsMode Mode => _snapshot.Mode;

    /// <summary>The latest snapshot.</summary>
    public DatabaseSessionSettingsSnapshot Snapshot => _snapshot;

    /// <summary>Records a verification result. The mode is kept.</summary>
    /// <param name="snapshot">The verification result.</param>
    public void SetSnapshot(DatabaseSessionSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot with { Mode = _snapshot.Mode };
    }
}
