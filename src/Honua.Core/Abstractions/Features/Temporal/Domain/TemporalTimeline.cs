// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

public sealed record TemporalRevision(
    long Generation,
    TemporalChangeKind Operation,
    DateTimeOffset ChangedAtUtc,
    TemporalAttribution? Attribution);

public sealed record TemporalTimeline(
    string ServiceId,
    int LayerId,
    long ObjectId,
    long CurrentGeneration,
    ImmutableArray<TemporalRevision> Revisions,
    string? NextCursor);

public sealed record TemporalTimelineRequest(
    long ObjectId,
    int? Limit = null,
    string? Cursor = null);
