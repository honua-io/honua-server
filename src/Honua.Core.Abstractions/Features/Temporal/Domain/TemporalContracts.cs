// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Temporal.Domain;

public sealed record TemporalCapability(
    string ServiceId,
    int LayerId,
    string? LayerName,
    bool SupportsHistory,
    bool SupportsAsOf,
    string? TemporalColumn,
    TemporalCursorKind CursorKind,
    long? CurrentGeneration,
    TemporalDeferredCapabilities DeferredCapabilities);

public sealed record TemporalDeferredCapabilities(
    bool SupportsDiff = false,
    bool SupportsTimeline = false,
    bool SupportsAttribution = false,
    bool SupportsRollback = false);

public enum TemporalCursorKind
{
    Generation = 0,

    // A timestamp checkpoint is rejected because the change tracker has no timestamp index.
    Timestamp = 1
}

public sealed record TemporalFeatureState(
    long ObjectId,
    TemporalChangeKind NetOperation,
    DateTimeOffset ChangedAtUtc,
    IReadOnlyDictionary<string, object?>? Attributes);

public enum TemporalChangeKind
{
    Insert = 1,

    Update = 2,

    Delete = 3
}

public sealed record TemporalAsOfResult(
    string ServiceId,
    int LayerId,
    TemporalCursorKind RequestedCursorKind,
    long ResolvedGeneration,
    long CurrentGeneration,
    ImmutableArray<TemporalFeatureState> Features);

public sealed record TemporalAsOfRequest(
    long? Generation = null,
    DateTimeOffset? Timestamp = null,
    int? Limit = null);
