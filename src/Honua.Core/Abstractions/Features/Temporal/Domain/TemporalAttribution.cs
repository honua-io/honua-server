// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Domain;

public enum TemporalAttributionSource
{
    Unknown = 0,

    EditSession = 1,

    Import = 2,

    Job = 3,

    Release = 4,

    ReplicaSync = 5,

    Rollback = 6
}

public sealed record TemporalAttribution(
    string? Actor,
    TemporalAttributionSource Source,
    string? Operation,
    string? SourceId);
