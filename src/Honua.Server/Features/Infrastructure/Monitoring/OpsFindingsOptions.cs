// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Infrastructure.Monitoring;

public sealed class OpsFindingsOptions
{
    public const string SectionName = "Observability:OpsFindings";

    [Range(1, long.MaxValue)]
    public long AlertDispatchPendingBacklogThreshold { get; set; } = 250;

    [Range(1, long.MaxValue)]
    public long AlertDispatchDeadLetterThreshold { get; set; } = 1;

    [Range(1, int.MaxValue)]
    public int GpQueueDepthThreshold { get; set; } = 200;

    [Range(0.01, 1.0)]
    public double DbPoolUtilizationThreshold { get; set; } = 0.9;

    [Range(1, long.MaxValue)]
    public long DbConnectionAcquisitionTimeoutThreshold { get; set; } = 5;

    [Range(0.01, 0.9)]
    public double DbBoundedAdmissionTuneDownFraction { get; set; } = 0.25;

    [Range(1, double.MaxValue)]
    public double ServingLatencyP95ThresholdMs { get; set; } = 2000;

    [Range(0.0001, 1.0)]
    public double ServingErrorRateThreshold { get; set; } = 0.05;

    [Range(1, double.MaxValue)]
    public double ServingLatencyP99ThresholdMs { get; set; } = 5000;

    [Range(1.0, double.MaxValue)]
    public double ServingErrorBudgetBurnRateThreshold { get; set; } = 2.0;

    [Range(1, long.MaxValue)]
    public long ServingLatencyMinSampleCount { get; set; } = 20;

    [Range(1, 1440)]
    public double ServingLatencyLookbackMinutes { get; set; } = 5;
}
