// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Configuration;

public sealed class GeoParquetLimits
{
    [Range(1, 10000)]
    public int MaxRowsPerBatch { get; set; } = 1024;

    [Range(1, int.MaxValue)]
    public long MaxEstimatedBatchBytes { get; set; } = 8 * 1024 * 1024;

    [Range(1, int.MaxValue)]
    public long MaxEstimatedInputBytes { get; set; } = 128 * 1024 * 1024;

    [Range(1, int.MaxValue)]
    public int MaxResponseBytes { get; set; } = 64 * 1024 * 1024;
}
