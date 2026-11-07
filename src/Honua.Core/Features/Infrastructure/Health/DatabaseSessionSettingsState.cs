// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Core.Features.Infrastructure.Health;

public enum DatabaseSessionSettingsVerificationStatus
{
    NotApplicable,

    Pending,

    Verified,

    Mismatch,

    Unavailable,
}

public sealed record DatabaseSessionSettingDrift(string Setting, string Expected, string? Actual, string Remediation);

public sealed record DatabaseSessionSettingsSnapshot
{
    public required DatabaseSessionSettingsMode Mode { get; init; }

    public required DatabaseSessionSettingsVerificationStatus Status { get; init; }

    public string? Role { get; init; }

    public IReadOnlyList<DatabaseSessionSettingDrift> Drift { get; init; } = Array.Empty<DatabaseSessionSettingDrift>();

    public string? Message { get; init; }
}

public sealed class DatabaseSessionSettingsState
{
    private volatile DatabaseSessionSettingsSnapshot _snapshot;

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

    public DatabaseSessionSettingsMode Mode => _snapshot.Mode;

    public DatabaseSessionSettingsSnapshot Snapshot => _snapshot;

    public void SetSnapshot(DatabaseSessionSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot with { Mode = _snapshot.Mode };
    }
}
