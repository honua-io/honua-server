// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Geoprocessing;

internal sealed class ExecutionAdmissionOptions
{
    public const string SectionName = "ExecutionAdmission";

    public bool Enabled { get; set; } = true;

    [Range(0, 10000, ErrorMessage = "MaxConcurrentJobsPerPartition must be between 0 and 10000")]
    public int MaxConcurrentJobsPerPartition { get; set; } = 10;

    [Range(0, 100000, ErrorMessage = "MaxConcurrentJobsGlobal must be between 0 and 100000")]
    public int MaxConcurrentJobsGlobal { get; set; } = 50;

    [Range(0, 100000, ErrorMessage = "MaxSubmissionsPerWindow must be between 0 and 100000")]
    public int MaxSubmissionsPerWindow { get; set; } = 20;

    [Range(1, 3600, ErrorMessage = "RateWindowSeconds must be between 1 and 3600")]
    public int RateWindowSeconds { get; set; } = 60;

    [Range(0, 100000, ErrorMessage = "MaxCostWeightPerPartition must be between 0 and 100000")]
    public double MaxCostWeightPerPartition { get; set; } = 20.0;

    [Range(1, 3600, ErrorMessage = "DefaultRetryAfterSeconds must be between 1 and 3600")]
    public int DefaultRetryAfterSeconds { get; set; } = 10;

    [Range(1, 300, ErrorMessage = "SharedLeaseSeconds must be between 1 and 300")]
    public int SharedLeaseSeconds { get; set; } = 30;

    [Range(10, 60000, ErrorMessage = "SharedLeaseAcquireTimeoutMilliseconds must be between 10 and 60000")]
    public int SharedLeaseAcquireTimeoutMilliseconds { get; set; } = 5000;
}
