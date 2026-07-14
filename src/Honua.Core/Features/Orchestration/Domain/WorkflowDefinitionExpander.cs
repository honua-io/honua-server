// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Deterministically unrolls a <see cref="WorkflowDefinition"/> containing
/// <see cref="WorkflowForEachSpec"/> steps into a flat definition of concrete steps —
/// one per iteration item — so the durable orchestration engine reconciles a plain DAG.
/// </summary>
/// <remarks>
/// <para>
/// The transform is pure and deterministic: the same input always yields the same
/// expanded step-set in the same order. The engine therefore applies it both when a run
/// is created (to materialise the run's step states) and on every reconcile tick (after
/// reloading the stored definition); because both sides expand identically, the step-set
/// consistency guard holds and no extra durable state is needed.
/// </para>
/// <para>
/// A ForEach step <c>F</c> over items <c>[a, b]</c> unrolls into sub-steps
/// <c>F::0</c> and <c>F::1</c>, each carrying a copy of the template's plan with the
/// item value substituted for <see cref="WorkflowForEachSpec.ItemPlaceholder"/>. A step
/// that depends on <c>F</c> fans in to depend on every sub-step, so its execution waits
/// for all iterations and the run aggregates the per-item outputs. Acyclicity is
/// preserved: the unroll only duplicates nodes along existing edges, so an acyclic
/// source graph stays acyclic.
/// </para>
/// </remarks>
public static class WorkflowDefinitionExpander
{
    /// <summary>The reserved separator joining a ForEach step id with its iteration index.</summary>
    public const string IterationSeparator = "::";

    /// <summary>
    /// Returns an expanded copy of <paramref name="definition"/> with every ForEach step
    /// unrolled into concrete per-item steps. A definition with no ForEach steps is
    /// returned unchanged.
    /// </summary>
    /// <param name="definition">The (already validated) definition to expand.</param>
    /// <returns>The flat, fully-unrolled definition.</returns>
    public static WorkflowDefinition Expand(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!definition.Steps.Any(static s => s.ForEach is not null))
        {
            return definition;
        }

        // Map every source step id to the concrete step ids it expands into. Non-ForEach
        // steps map to themselves; ForEach steps map to their ordered per-item instances.
        var expandedIds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (step.ForEach is { } forEach)
            {
                var count = Math.Min(forEach.Items.Count, WorkflowForEachSpec.HardIterationLimit);
                var ids = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    ids.Add(IterationStepId(step.StepId, i));
                }

                expandedIds[step.StepId] = ids;
            }
            else
            {
                expandedIds[step.StepId] = [step.StepId];
            }
        }

        var expandedSteps = new List<WorkflowStepDefinition>(definition.Steps.Count);
        foreach (var step in definition.Steps)
        {
            if (step.ForEach is { } forEach)
            {
                var count = Math.Min(forEach.Items.Count, WorkflowForEachSpec.HardIterationLimit);
                for (var i = 0; i < count; i++)
                {
                    expandedSteps.Add(BuildIterationStep(step, forEach, i, expandedIds));
                }
            }
            else
            {
                expandedSteps.Add(step with
                {
                    DependsOn = RemapDependencies(step.DependsOn, expandedIds),
                    InputBindings = RemapBindings(step.InputBindings, expandedIds)
                });
            }
        }

        return definition with { Steps = expandedSteps };
    }

    private static WorkflowStepDefinition BuildIterationStep(
        WorkflowStepDefinition template,
        WorkflowForEachSpec forEach,
        int index,
        IReadOnlyDictionary<string, List<string>> expandedIds)
    {
        var item = forEach.Items[index];
        var stepId = IterationStepId(template.StepId, index);

        return template with
        {
            StepId = stepId,
            ForEach = null,
            Plan = SubstituteItem(template.Plan, forEach.ItemPlaceholder, item, index),
            DependsOn = RemapDependencies(template.DependsOn, expandedIds),
            InputBindings = RemapBindings(template.InputBindings, expandedIds)
        };
    }

    private static string IterationStepId(string baseStepId, int index)
        => string.Concat(baseStepId, IterationSeparator, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static List<string> RemapDependencies(
        IReadOnlyList<string> dependsOn,
        IReadOnlyDictionary<string, List<string>> expandedIds)
    {
        if (dependsOn.Count == 0)
        {
            return [];
        }

        var remapped = new List<string>(dependsOn.Count);
        foreach (var dependency in dependsOn)
        {
            if (expandedIds.TryGetValue(dependency, out var ids))
            {
                remapped.AddRange(ids);
            }
            else
            {
                remapped.Add(dependency);
            }
        }

        return remapped;
    }

    private static List<StepInputBinding> RemapBindings(
        IReadOnlyList<StepInputBinding> bindings,
        IReadOnlyDictionary<string, List<string>> expandedIds)
    {
        if (bindings.Count == 0)
        {
            return [];
        }

        var remapped = new List<StepInputBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            // Binding from a ForEach source is rejected at validation; if a single-instance
            // source was remapped (non-ForEach maps to itself) we keep it, otherwise we bind
            // from the first instance defensively so the transform stays total.
            var sourceId = expandedIds.TryGetValue(binding.SourceStepId, out var ids) && ids.Count > 0
                ? ids[0]
                : binding.SourceStepId;
            remapped.Add(binding with { SourceStepId = sourceId });
        }

        return remapped;
    }

    private static AnalysisPlan SubstituteItem(AnalysisPlan plan, string placeholder, string item, int index)
    {
        var suffixedPlanId = string.Concat(plan.PlanId, IterationSeparator, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (string.IsNullOrEmpty(placeholder) || plan.Steps.Count == 0)
        {
            return plan with { PlanId = suffixedPlanId };
        }

        var steps = new AnalysisPlanStep[plan.Steps.Count];
        for (var s = 0; s < plan.Steps.Count; s++)
        {
            var step = plan.Steps[s];
            if (step.Inputs.Count == 0)
            {
                steps[s] = step;
                continue;
            }

            Dictionary<string, string>? substituted = null;
            foreach (var pair in step.Inputs.Where(p => p.Value.Contains(placeholder, StringComparison.Ordinal)))
            {
                substituted ??= new Dictionary<string, string>(step.Inputs, StringComparer.Ordinal);
                substituted[pair.Key] = pair.Value.Replace(placeholder, item, StringComparison.Ordinal);
            }

            steps[s] = substituted is null ? step : step with { Inputs = substituted };
        }

        return plan with { PlanId = suffixedPlanId, Steps = steps };
    }
}
