// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal enum MigrationLifecycleStatus
{
    Unknown = 0,
    Running = 1,
    Succeeded = 2,
    Skipped = 3,
    Failed = 4
}

/// <summary>
/// Tracks the outcome of database migrations for readiness checks.
/// </summary>
internal sealed class MigrationState
{
    private int _status;
    private string? _statusMessage;

    public MigrationLifecycleStatus Status => (MigrationLifecycleStatus)Volatile.Read(ref _status);

    public bool IsReady => Status is MigrationLifecycleStatus.Succeeded or MigrationLifecycleStatus.Skipped;

    public bool IsFailed => Status == MigrationLifecycleStatus.Failed;

    public bool IsRunning => Status == MigrationLifecycleStatus.Running;

    public string? StatusMessage => _statusMessage;

    public string? FailureMessage => IsFailed ? _statusMessage : null;

    public void MarkRunning(string? message = null)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Running);
    }

    public void MarkSucceeded(string? message = null)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Succeeded);
    }

    public void MarkSkipped(string? message = null)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Skipped);
    }

    public void MarkFailed(string? message)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Failed);
    }
}
