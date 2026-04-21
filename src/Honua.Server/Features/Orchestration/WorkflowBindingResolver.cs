// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Resolves declarative step input bindings into concrete analysis-plan input values
/// using the captured output artifacts of upstream steps.
/// </summary>
internal static class WorkflowBindingResolver
{
    private const string ArtifactSelectorPrefix = "artifact:";

    public sealed record BindingResolution(
        IReadOnlyDictionary<string, string> ResolvedValues,
        IReadOnlyList<string> Failures);

    public static BindingResolution Resolve(
        WorkflowStepDefinition step,
        IReadOnlyDictionary<string, WorkflowStepState> upstreamByStepId)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(upstreamByStepId);

        if (step.InputBindings.Count == 0)
        {
            return new BindingResolution(
                new Dictionary<string, string>(StringComparer.Ordinal),
                Array.Empty<string>());
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var binding in step.InputBindings)
        {
            if (!upstreamByStepId.TryGetValue(binding.SourceStepId, out var upstream))
            {
                failures.Add($"Binding '{binding.TargetInputKey}' references unknown upstream step '{binding.SourceStepId}'.");
                continue;
            }

            var artifact = SelectArtifact(upstream.OutputArtifacts, binding.SourceArtifactSelector);
            if (artifact is null)
            {
                failures.Add(
                    $"Binding '{binding.TargetInputKey}' could not resolve selector '{binding.SourceArtifactSelector}' from step '{binding.SourceStepId}'.");
                continue;
            }

            resolved[binding.TargetInputKey] = artifact.Uri ?? artifact.ArtifactId;
        }

        return new BindingResolution(resolved, failures);
    }

    public static ArtifactRef? SelectArtifact(
        IReadOnlyList<ArtifactRef>? artifacts,
        string selector)
    {
        if (artifacts is null || artifacts.Count == 0 || string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        if (!selector.StartsWith(ArtifactSelectorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = selector[ArtifactSelectorPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        if (int.TryParse(target, out var index))
        {
            return index >= 0 && index < artifacts.Count ? artifacts[index] : null;
        }

        foreach (var artifact in artifacts)
        {
            if (string.Equals(artifact.Label, target, StringComparison.OrdinalIgnoreCase))
            {
                return artifact;
            }
        }

        return null;
    }

    public static AnalysisPlan ApplyBindings(AnalysisPlan plan, IReadOnlyDictionary<string, string> resolvedValues)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (resolvedValues.Count == 0 || plan.Steps.Count == 0)
        {
            return plan;
        }

        // Apply resolved inputs to the first executable step of the plan. The canonical
        // AnalysisPlanStep dictionary is the transport surface the job substrate reads,
        // so overriding there keeps the engine free of opinion about downstream routing.
        var firstStep = plan.Steps[0];
        var merged = new Dictionary<string, string>(firstStep.Inputs, StringComparer.Ordinal);
        foreach (var pair in resolvedValues)
        {
            merged[pair.Key] = pair.Value;
        }

        var steps = plan.Steps.ToArray();
        steps[0] = firstStep with { Inputs = merged };
        return plan with { Steps = steps };
    }
}
