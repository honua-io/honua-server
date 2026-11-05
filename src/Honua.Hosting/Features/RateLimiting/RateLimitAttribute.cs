// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.RateLimiting;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RateLimitAttribute : Attribute
{
    public RateLimitAttribute(int requestsPerMinute)
    {
        RequestsPerMinute = requestsPerMinute;
    }

    public int RequestsPerMinute { get; }
}
