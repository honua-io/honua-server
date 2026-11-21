// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Monitoring;

internal enum MigrationLifecycleStatus
{
    Unknown = 0,
    Running = 1,
    Succeeded = 2,
    Skipped = 3,
    Failed = 4
}

internal sealed class MigrationState
{
    private int _status;
    private int _transitions;
    private string? _statusMessage;

    public MigrationLifecycleStatus Status => (MigrationLifecycleStatus)Volatile.Read(ref _status);

    public bool IsReady => Status is MigrationLifecycleStatus.Succeeded or MigrationLifecycleStatus.Skipped;

    public bool IsFailed => Status == MigrationLifecycleStatus.Failed;

    public bool IsRunning => Status == MigrationLifecycleStatus.Running;

    public string? StatusMessage => _statusMessage;

    // Advances on every Mark* call, including one that repeats the current status, so a reader
    // can tell that migration work happened even when the status reads the same afterwards.
    public int Transitions => Volatile.Read(ref _transitions);

    public string? FailureMessage => IsFailed ? _statusMessage : null;

    public void MarkRunning(string? message = null)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Running);
        Interlocked.Increment(ref _transitions);
    }

    public void MarkSucceeded(string? message = null)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Succeeded);
        Interlocked.Increment(ref _transitions);
    }

    public void MarkSkipped(string? message = null)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Skipped);
        Interlocked.Increment(ref _transitions);
    }

    public void MarkFailed(string? message)
    {
        _statusMessage = message;
        Volatile.Write(ref _status, (int)MigrationLifecycleStatus.Failed);
        Interlocked.Increment(ref _transitions);
    }
}
