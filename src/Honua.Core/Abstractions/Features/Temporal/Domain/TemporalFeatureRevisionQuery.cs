// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Domain;

public sealed record TemporalFeatureRevisionQuery
{
    public int StorageLayerId { get; init; }
    public long ObjectId { get; init; }
    public long AfterGeneration { get; init; }
    public long ThroughGeneration { get; init; }
    public int Limit { get; init; }
}
