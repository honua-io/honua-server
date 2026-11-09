// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

public enum ShareExportDestinationType
{
    S3,

    Sftp,

    Webhook,

    AuditSnapshot
}

public enum ShareExportDestinationStatus
{
    Supported,

    Unsupported,

    NotConfigured
}

public enum ShareExportScheduleState
{
    Active,

    Paused
}

public enum ShareExportTriggerKind
{
    Scheduled,

    Manual
}

public enum ShareExportRunStatus
{
    Queued,

    Running,

    Succeeded,

    Failed,

    Cancelled
}
