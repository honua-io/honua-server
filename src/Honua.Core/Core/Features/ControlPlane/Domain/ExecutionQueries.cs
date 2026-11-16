// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public sealed record ExecutionJobQuery
{
    public IReadOnlyList<ExecutionJobStatus> Statuses { get; init; } = Array.Empty<ExecutionJobStatus>();

    public ExecutionJobKind? Kind { get; init; }

    public string? Backend { get; init; }

    public string? Queue { get; init; }

    public string? RequestedBy { get; init; }

    public bool ApplyTenantScope { get; init; }

    public string? TenantId { get; init; }

    public bool IncludeUnassignedTenant { get; init; }

    public string? CorrelationId { get; init; }

    public string? TraceId { get; init; }

    public string? DefinitionId { get; init; }

    public string? ResourceRef { get; init; }

    public string? Environment { get; init; }

    public string? Server { get; init; }

    public string? ReleaseId { get; init; }

    public string? ChangeSetId { get; init; }

    public string? AlertId { get; init; }

    public DateTimeOffset? CreatedFrom { get; init; }

    public DateTimeOffset? CreatedTo { get; init; }

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed record ExecutionJobPage
{
    public required IReadOnlyList<ExecutionJobRecord> Items { get; init; }

    public string? NextCursor { get; init; }
}

public sealed record ExecutionLogQuery
{
    public string? Cursor { get; init; }

    public int Limit { get; init; } = 100;
}

public sealed record ExecutionLogPage
{
    public int DroppedCount { get; init; }

    public required IReadOnlyList<ExecutionLogEntry> Items { get; init; }

    public string? NextCursor { get; init; }

    public int TotalCount { get; init; }
}

public sealed record ExecutionLogTail
{
    public int DroppedCount { get; init; }

    public required IReadOnlyList<ExecutionLogEntry> Items { get; init; }

    public required int TotalCount { get; init; }
}
