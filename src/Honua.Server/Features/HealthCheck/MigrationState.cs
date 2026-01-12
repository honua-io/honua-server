// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

/// <summary>
/// Tracks the outcome of database migrations for readiness checks.
/// </summary>
internal sealed class MigrationState
{
    private int _status; // 0 = unknown, 1 = succeeded, 2 = skipped, 3 = failed
    private string? _failureMessage;

    public bool IsReady => Volatile.Read(ref _status) is 1 or 2;

    public bool IsFailed => Volatile.Read(ref _status) == 3;

    public string? FailureMessage => _failureMessage;

    public void MarkSucceeded()
    {
        _failureMessage = null;
        Volatile.Write(ref _status, 1);
    }

    public void MarkSkipped()
    {
        _failureMessage = null;
        Volatile.Write(ref _status, 2);
    }

    public void MarkFailed(string? message)
    {
        _failureMessage = message;
        Volatile.Write(ref _status, 3);
    }
}
