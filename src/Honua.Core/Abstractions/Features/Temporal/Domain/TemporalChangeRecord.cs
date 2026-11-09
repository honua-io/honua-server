// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Domain;

public sealed record TemporalChangeRecord(
    long Generation,
    long ObjectId,
    TemporalChangeKind Operation,
    DateTimeOffset ChangedAtUtc,
    TemporalAttribution? Attribution);
