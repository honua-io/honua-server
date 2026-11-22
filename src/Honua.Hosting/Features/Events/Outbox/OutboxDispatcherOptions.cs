// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events.Outbox;

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "FeatureChangeEvents:Outbox";

    public int BatchSize { get; set; } = 32;

    public int IdlePollIntervalMs { get; set; } = 1_000;

    // Empty passes double the wait from IdlePollIntervalMs up to this cap. A local outbox write returns the
    // dispatcher to IdlePollIntervalMs; rows from processes without a dispatcher (execution workers) wait at
    // most this cap. Values below IdlePollIntervalMs are treated as IdlePollIntervalMs (fixed polling).
    public int MaxIdlePollIntervalMs { get; set; } = 10_000;

    public int ClaimTtlSeconds { get; set; } = 30;

    public int RecoveryIntervalSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 5;

    public int DegradedBacklogThreshold { get; set; } = 1_000;

    public int UnhealthyDeadLetterThreshold { get; set; } = 1;
}
