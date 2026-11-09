// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Deployment.Domain;

public sealed record RuntimeState
{
    public RuntimeHealth Health { get; init; } = RuntimeHealth.Unknown;

    public string? PublicUrl { get; init; }

    public DateTimeOffset? LastObservedAt { get; init; }

    public DateTimeOffset? LastHealthCheckAt { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static RuntimeState Unknown() => new();

    public static RuntimeState Healthy(string? publicUrl = null, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        return new RuntimeState
        {
            Health = RuntimeHealth.Healthy,
            PublicUrl = publicUrl,
            LastObservedAt = now,
            LastHealthCheckAt = now
        };
    }

    public static RuntimeState Degraded(string message, string? publicUrl = null, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        return new RuntimeState
        {
            Health = RuntimeHealth.Degraded,
            PublicUrl = publicUrl,
            LastObservedAt = now,
            LastHealthCheckAt = now,
            Message = message
        };
    }

    public static RuntimeState Unhealthy(string message, string? publicUrl = null, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        return new RuntimeState
        {
            Health = RuntimeHealth.Unhealthy,
            PublicUrl = publicUrl,
            LastObservedAt = now,
            LastHealthCheckAt = now,
            Message = message
        };
    }
}
