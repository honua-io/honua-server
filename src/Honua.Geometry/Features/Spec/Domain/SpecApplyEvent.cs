// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public enum SpecApplyEventKind
{
    Queued,

    Running,

    Cached,

    Succeeded,

    Failed,

    Skipped,

    Warning,

    ApplyStarted,

    ApplyCompleted,

    ApplyCancelled
}

public sealed record SpecApplyEvent
{
    public required long Sequence { get; init; }

    public required SpecApplyEventKind Kind { get; init; }

    public required string ApplyToken { get; init; }

    public string? NodeId { get; init; }

    public string? ContentHash { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public SpecWarning? Diagnostic { get; init; }

    public SpecCostActual? ActualCost { get; init; }

    public SpecApplySummary? Summary { get; init; }
}

public sealed record SpecCostActual
{
    public long? Rows { get; init; }

    public long? Bytes { get; init; }

    public required double DurationMs { get; init; }
}

public sealed record SpecApplySummary
{
    public required int TotalNodes { get; init; }

    public required int CachedNodes { get; init; }

    public required int RanNodes { get; init; }

    public required int FailedNodes { get; init; }

    public required int SkippedNodes { get; init; }

    public required double TotalDurationMs { get; init; }

    public required bool Cancelled { get; init; }
}
