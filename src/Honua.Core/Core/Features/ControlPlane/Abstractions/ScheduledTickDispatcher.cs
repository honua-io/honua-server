// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Abstractions;

public enum ScheduledTickKind
{
    WorkflowSchedule,

    JobReconciliation,

    TileCacheExpiry,

    WorkspaceCleanup,

    FileStorageCleanup,

    TemporaryFileCleanup,

    DigestFlush,

    GeoprocessingOutputSweep,

    // Job store retention sweep (JobStoreRetentionSweeper.SweepAsync): deletes terminal jobs, job
    // logs, result packages and leases past their retention. Cadence minutes. Every delete is
    // bounded and skips locked rows, so duplicate or concurrent sweeps delete nothing extra.
    JobStoreRetention,

    // Leased operational reminders and approval expiry. Cadence one minute.
    OpsNotifications
}

public interface IScheduledTickHandler
{
    ScheduledTickKind Kind { get; }

    Task RunTickAsync(CancellationToken cancellationToken = default);
}

public interface IScheduledTickDispatcher
{
    Task RunTickAsync(ScheduledTickKind kind, CancellationToken cancellationToken = default);
}
