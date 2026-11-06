// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Infrastructure.RateLimiting;

internal sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    // Application limiting is opt-in because the baseline posture enforces limits at the edge.
    public bool Enabled { get; set; }

    [Range(1, 100000, ErrorMessage = "GlobalRequestsPerMinute must be between 1 and 100000")]
    public int GlobalRequestsPerMinute { get; set; } = 1000;

    [Range(1, 10000, ErrorMessage = "QueryRequestsPerMinute must be between 1 and 10000")]
    public int QueryRequestsPerMinute { get; set; } = 100;

    [Range(1, 1000, ErrorMessage = "UploadRequestsPerMinute must be between 1 and 1000")]
    public int UploadRequestsPerMinute { get; set; } = 10;

    [Range(1, 10000, ErrorMessage = "MetadataRequestsPerMinute must be between 1 and 10000")]
    public int MetadataRequestsPerMinute { get; set; } = 200;

    public bool UseDistributedRateLimiting { get; set; } = true;

    public string KeyPrefix { get; set; } = "honua:ratelimit:";

    public bool IncludeHeaders { get; set; } = true;

    [Range(400, 599, ErrorMessage = "StatusCode must be a valid HTTP status code")]
    public int StatusCode { get; set; } = 429;
}
