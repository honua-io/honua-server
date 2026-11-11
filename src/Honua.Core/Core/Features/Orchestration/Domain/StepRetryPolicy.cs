// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Orchestration.Domain;

[JsonConverter(typeof(StepRetryPolicyJsonConverter))]
public sealed record StepRetryPolicy
{
    public required int MaxAttempts { get; init; }

    public required int InitialDelaySeconds { get; init; }

    public double BackoffMultiplier { get; init; } = 2.0;

    public int MaxDelaySeconds { get; init; } = 300;

    public TimeSpan ComputeDelay(int completedAttempts)
    {
        if (completedAttempts <= 0 || InitialDelaySeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var multiplier = BackoffMultiplier <= 0 ? 1.0 : BackoffMultiplier;
        var delay = InitialDelaySeconds * Math.Pow(multiplier, completedAttempts - 1);
        var clamped = Math.Min(delay, MaxDelaySeconds <= 0 ? delay : MaxDelaySeconds);
        return TimeSpan.FromSeconds(clamped);
    }
}
