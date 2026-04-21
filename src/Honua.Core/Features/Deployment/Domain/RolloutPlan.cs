// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Declarative rollout plan describing how a deployment reaches its final serving
/// weight. The plan is immutable for the lifetime of a deployment; observed rollout
/// progress is recorded on the <see cref="Deployment"/> itself.
/// </summary>
public sealed record RolloutPlan
{
    /// <summary>
    /// Strategy governing how traffic shifts from the previous deployment to this one.
    /// </summary>
    public required RolloutStrategy Strategy { get; init; }

    /// <summary>
    /// Ordered canary weight percentages when <see cref="Strategy"/> is
    /// <see cref="RolloutStrategy.Canary"/>. Each step is in the inclusive range [1, 100],
    /// strictly increasing, and the final step is 100. Empty for non-canary strategies.
    /// </summary>
    public IReadOnlyList<int> Steps { get; init; } = [];

    /// <summary>
    /// Whether the rollout should advance automatically through steps without operator action.
    /// </summary>
    public bool AutoPromote { get; init; } = true;

    /// <summary>
    /// Returns a rollout plan that cuts over in a single step.
    /// </summary>
    public static RolloutPlan Immediate() => new() { Strategy = RolloutStrategy.Immediate, AutoPromote = true };

    /// <summary>
    /// Returns a blue-green rollout plan that stages the new deployment before cutover.
    /// </summary>
    /// <param name="autoPromote">Whether the cutover happens automatically once the new deployment is ready.</param>
    public static RolloutPlan BlueGreen(bool autoPromote = true)
        => new() { Strategy = RolloutStrategy.BlueGreen, AutoPromote = autoPromote };

    /// <summary>
    /// Returns a canary rollout plan advancing through the given weight steps.
    /// </summary>
    /// <param name="steps">Ordered canary weight percentages. Must be strictly increasing, each in [1, 100], and end at 100.</param>
    /// <param name="autoPromote">Whether steps advance automatically.</param>
    /// <exception cref="ArgumentException">Thrown when the steps are empty, unordered, out of range, or do not end at 100.</exception>
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

    /// <summary>
    /// Returns a schedule-gated rollout plan that promotes only after a publication schedule fires.
    /// </summary>
    public static RolloutPlan Scheduled()
        => new() { Strategy = RolloutStrategy.Scheduled, AutoPromote = false };
}
