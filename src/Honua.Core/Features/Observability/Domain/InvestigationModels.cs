// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Observability.Domain;

public enum InvestigationStatus
{
    Open = 0,

    Closed = 1,
}

public enum InvestigationResourceKind
{
    Alert = 1,

    Job = 2,

    Release = 3,

    ChangeSet = 4,
}

public sealed record InvestigationPin
{
    public required long PinId { get; init; }

    public required string EventRef { get; init; }

    public required OperateEventKind EventKind { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

public sealed record InvestigationLink
{
    public required long LinkId { get; init; }

    public required InvestigationResourceKind ResourceKind { get; init; }

    public required string ResourceId { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

public sealed record InvestigationRecord
{
    public required string InvestigationId { get; init; }

    public required string Title { get; init; }

    public required InvestigationStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? Summary { get; init; }

    public ImmutableArray<InvestigationPin> Pins { get; init; } = ImmutableArray<InvestigationPin>.Empty;

    public ImmutableArray<InvestigationLink> Links { get; init; } = ImmutableArray<InvestigationLink>.Empty;
}

public sealed record InvestigationFilter
{
    public InvestigationStatus? Status { get; init; }

    public string? CreatedBy { get; init; }

    public string? TitleContains { get; init; }

    public int PageSize { get; init; } = 50;

    public string? Cursor { get; init; }
}

public sealed record InvestigationSummary
{
    public required string InvestigationId { get; init; }

    public required string Title { get; init; }

    public required InvestigationStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? Summary { get; init; }

    public required int PinCount { get; init; }

    public required int LinkCount { get; init; }
}

public sealed record InvestigationPage
{
    public required IReadOnlyList<InvestigationSummary> Items { get; init; }

    public string? NextCursor { get; init; }
}
