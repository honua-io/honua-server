// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

public sealed class PerformanceMonitoringOptions
{
    public bool EnableMemoryTracking { get; set; } = true;

    public TimeSpan SlowRequestThreshold { get; set; } = TimeSpan.FromSeconds(1);

    public int MemorySamplingInterval { get; set; } = 100;

    public bool EnableDetailedRequestTracking { get; set; } = true;
}
