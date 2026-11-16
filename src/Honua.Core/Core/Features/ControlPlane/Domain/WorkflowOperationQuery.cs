// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public sealed record WorkflowOperationQuery
{
    public WorkflowOperationKind? Kind { get; init; }

    public WorkflowOperationStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

public sealed record WorkflowOperationPage
{
    public required IReadOnlyList<WorkflowOperationRecord> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int TotalCount { get; init; }

    public bool HasMore { get; init; }

    public bool IsTruncated { get; init; }
}
