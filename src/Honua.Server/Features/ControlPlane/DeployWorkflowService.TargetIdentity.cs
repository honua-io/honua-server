// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class DeployWorkflowService
{
    // Parameters that say which infrastructure a target is (cluster, service, region, function,
    // namespace, compose project, resource address) belong to the registered target. A request may tune rollout and
    // gate parameters, but may not point the target at different infrastructure.
    private static readonly string[] TargetIdentityParameterPrefixes =
        ["aws.", "lambda.", "kubernetes.", "k8s.", "compose.", "target."];

    // Backend-specific rollout pacing stays tunable per request, like the deployment.* keys.
    private static readonly string[] RolloutTuningParameterSuffixes =
        ["canary_weight_percentage", "_attempts", "_delay_ms"];

    internal const string TargetIdentityOverrideMessage =
        "Deploy requests cannot change the target's infrastructure parameters; change the deploy target definition instead:";

    private static DeployPlan ApplyTargetIdentityOverrideBlock(
        DeployPlan plan,
        DeployTargetDefinition target,
        IReadOnlyDictionary<string, string>? parameterOverrides)
    {
        var changed = (parameterOverrides ?? new Dictionary<string, string>())
            .Where(entry => IsTargetIdentityParameter(entry.Key) && !MatchesTargetValue(target, entry))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (changed.Length == 0)
        {
            return plan;
        }

        return plan with
        {
            IsReadyToSubmit = false,
            BlockingReasons = [.. plan.BlockingReasons, $"{TargetIdentityOverrideMessage} {string.Join(", ", changed)}."]
        };
    }

    private static bool IsTargetIdentityParameter(string key)
        => TargetIdentityParameterPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            && !RolloutTuningParameterSuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal));

    private static bool MatchesTargetValue(DeployTargetDefinition target, KeyValuePair<string, string> entry)
        => target.Parameters.TryGetValue(entry.Key, out var configured)
            && string.Equals(configured, entry.Value, StringComparison.Ordinal);
}
