// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

/// <summary>
/// Destination families a scheduled Share export can target.
/// </summary>
public enum ShareExportDestinationType
{
    /// <summary>Amazon S3 (or S3-compatible) object storage destination.</summary>
    S3,

    /// <summary>SFTP file transfer destination.</summary>
    Sftp,

    /// <summary>HTTP webhook delivery destination.</summary>
    Webhook,

    /// <summary>Point-in-time Share access audit snapshot destination.</summary>
    AuditSnapshot
}

/// <summary>
/// Resolved availability of a destination family in the current build and environment.
/// </summary>
public enum ShareExportDestinationStatus
{
    /// <summary>A worker is registered for this destination and execution can be backed by the job runner.</summary>
    Supported,

    /// <summary>No worker is registered for this destination family in the current build.</summary>
    Unsupported,

    /// <summary>The destination family is known but lacks the credentials or configuration required to run.</summary>
    NotConfigured
}

/// <summary>
/// Whether a scheduled export's automatic firing is active or paused.
/// </summary>
public enum ShareExportScheduleState
{
    /// <summary>The schedule is active and reflects intent to fire on its cron expression.</summary>
    Active,

    /// <summary>The schedule is paused and will not fire automatically.</summary>
    Paused
}

/// <summary>
/// What initiated an export run.
/// </summary>
public enum ShareExportTriggerKind
{
    /// <summary>The run was initiated by the schedule.</summary>
    Scheduled,

    /// <summary>The run was initiated by an explicit manual trigger.</summary>
    Manual
}

/// <summary>
/// Lifecycle state of an individual export run.
/// </summary>
public enum ShareExportRunStatus
{
    /// <summary>The run is queued and not yet started.</summary>
    Queued,

    /// <summary>The run is actively executing.</summary>
    Running,

    /// <summary>The run completed successfully.</summary>
    Succeeded,

    /// <summary>The run failed.</summary>
    Failed,

    /// <summary>The run was cancelled.</summary>
    Cancelled
}
