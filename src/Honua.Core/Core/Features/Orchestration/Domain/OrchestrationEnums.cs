// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

public enum WorkflowRunStatus
{
    Pending,

    Running,

    Succeeded,

    Failed,

    Cancelled
}

public enum WorkflowStepStatus
{
    Pending,

    Queued,

    Running,

    Succeeded,

    Failed,

    Cancelled,

    Skipped
}

public enum WorkflowStepFailurePolicy
{
    Fail,

    Skip
}

public enum WorkflowTriggerKind
{
    Manual,

    Cron,

    ChangeFeed,

    ObjectStore
}

public enum WorkflowCancellationOutcome
{
    NotFound,

    AlreadyTerminal,

    AlreadyCancelled,

    CancellationRequested,

    LeaseContention
}
