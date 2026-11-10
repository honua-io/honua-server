// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Deployment.Domain;

public sealed record RolloutPlan
{
    public required RolloutStrategy Strategy { get; init; }

    public IReadOnlyList<int> Steps { get; init; } = [];

    public bool AutoPromote { get; init; } = true;

    public static RolloutPlan Immediate() => new() { Strategy = RolloutStrategy.Immediate, AutoPromote = true };

    public static RolloutPlan BlueGreen(bool autoPromote = true)
        => new() { Strategy = RolloutStrategy.BlueGreen, AutoPromote = autoPromote };

    public static RolloutPlan Canary(IReadOnlyList<int> steps, bool autoPromote = true)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            throw new ArgumentException("Canary rollout requires at least one step.", nameof(steps));
        }

        var previous = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step is < 1 or > 100)
            {
                throw new ArgumentException(
                    $"Canary step {i} value {step} is outside the inclusive range [1, 100].",
                    nameof(steps));
            }

            if (step <= previous)
            {
                throw new ArgumentException(
                    $"Canary steps must strictly increase; step {i} ({step}) is not greater than previous ({previous}).",
                    nameof(steps));
            }

            previous = step;
        }

        if (steps[^1] != 100)
        {
            throw new ArgumentException(
                $"Final canary step must be 100; got {steps[^1]}.",
                nameof(steps));
        }

        return new RolloutPlan
        {
            Strategy = RolloutStrategy.Canary,
            Steps = steps.ToArray(),
            AutoPromote = autoPromote
        };
    }

    public static RolloutPlan Scheduled()
        => new() { Strategy = RolloutStrategy.Scheduled, AutoPromote = false };
}
