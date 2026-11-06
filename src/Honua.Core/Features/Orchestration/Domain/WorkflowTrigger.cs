// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record WorkflowTrigger
{
    public required WorkflowTriggerKind Kind { get; init; }

    public string? CronExpression { get; init; }

    public string? TimeZone { get; init; }

    public IReadOnlyList<int> WatchedLayerIds { get; init; } = [];

    public ObjectStoreTriggerConfig? ObjectStore { get; init; }

    public bool Enabled { get; init; } = true;
}

public sealed record ObjectStoreTriggerConfig
{
    public required string StoreId { get; init; }

    public string Prefix { get; init; } = string.Empty;

    public TimeSpan? PollInterval { get; init; }
}
