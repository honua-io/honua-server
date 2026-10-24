// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.RateLimiting;

/// <summary>
/// Attribute for specifying endpoint-specific rate limits.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RateLimitAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitAttribute"/> class.
    /// </summary>
    /// <param name="requestsPerMinute">Requests per minute for this endpoint.</param>
    public RateLimitAttribute(int requestsPerMinute)
    {
        RequestsPerMinute = requestsPerMinute;
    }

    /// <summary>
    /// Gets the requests per minute limit for the endpoint.
    /// </summary>
    public int RequestsPerMinute { get; }
}
