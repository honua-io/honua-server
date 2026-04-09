// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Infrastructure.RateLimiting;

/// <summary>
/// Configuration options for rate limiting middleware.
/// </summary>
internal sealed class RateLimitingOptions
{
    /// <summary>
    /// Configuration section name for binding from environment variables.
    /// </summary>
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Whether rate limiting is enabled. Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Global rate limit for requests per minute per IP/API key.
    /// Default is 1000 requests per minute.
    /// </summary>
    [Range(1, 100000, ErrorMessage = "GlobalRequestsPerMinute must be between 1 and 100000")]
    public int GlobalRequestsPerMinute { get; set; } = 1000;

    /// <summary>
    /// Rate limit for query endpoints per minute.
    /// Default is 100 requests per minute.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "QueryRequestsPerMinute must be between 1 and 10000")]
    public int QueryRequestsPerMinute { get; set; } = 100;

    /// <summary>
    /// Rate limit for file upload endpoints per minute.
    /// Default is 10 requests per minute.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "UploadRequestsPerMinute must be between 1 and 1000")]
    public int UploadRequestsPerMinute { get; set; } = 10;

    /// <summary>
    /// Rate limit for metadata endpoints per minute.
    /// Default is 200 requests per minute.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "MetadataRequestsPerMinute must be between 1 and 10000")]
    public int MetadataRequestsPerMinute { get; set; } = 200;

    /// <summary>
    /// Whether to use distributed rate limiting with Redis.
    /// Default is true if Redis is available.
    /// </summary>
    public bool UseDistributedRateLimiting { get; set; } = true;

    /// <summary>
    /// Key prefix for rate limiting cache entries.
    /// Default is "honua:ratelimit:".
    /// </summary>
    public string KeyPrefix { get; set; } = "honua:ratelimit:";

    /// <summary>
    /// Whether to include rate limit headers in responses.
    /// Default is true.
    /// </summary>
    public bool IncludeHeaders { get; set; } = true;

    /// <summary>
    /// Response status code for rate limit exceeded.
    /// Default is 429 (Too Many Requests).
    /// </summary>
    [Range(400, 599, ErrorMessage = "StatusCode must be a valid HTTP status code")]
    public int StatusCode { get; set; } = 429;
}
