// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

public enum TemporalDiffChangeClass
{
    Added = 1,

    Removed = 2,

    AttributeChanged = 3,

    GeometryChanged = 4
}

public sealed record TemporalFieldChange(
    string Field,
    object? OldValue,
    object? NewValue,
    bool Masked = false);

public sealed record TemporalFeatureDiff(
    long ObjectId,
    TemporalDiffChangeClass PrimaryClass,
    ImmutableArray<TemporalDiffChangeClass> Classes,
    bool GeometryChanged,
    ImmutableArray<TemporalFieldChange> FieldChanges,
    TemporalAttribution? Attribution = null);

public sealed record TemporalDiffSummary(
    int Added,
    int Removed,
    int AttributeChanged,
    int GeometryChanged,
    int Total);

public sealed record TemporalDiffResult(
    string ServiceId,
    int LayerId,
    ResolvedTemporalCheckpoint FromCheckpoint,
    ResolvedTemporalCheckpoint ToCheckpoint,
    TemporalDiffSummary Summary,
    ImmutableArray<TemporalFeatureDiff> Changes,
    string? NextCursor);

public sealed record TemporalDiffRequest(
    TemporalCheckpoint From,
    TemporalCheckpoint? To = null,
    int? Limit = null,
    string? Cursor = null);
