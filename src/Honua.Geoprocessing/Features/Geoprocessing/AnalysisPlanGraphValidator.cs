// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

// The executor assumes a topological order, so a cycle or a dangling reference would deadlock or misorder the run.
internal static class AnalysisPlanGraphValidator
{
    public static void Validate(AnalysisPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Proto-level enum validation happens in the conversion layer. The remaining
        // domain-level invariant is that the step dependency graph be acyclic and that
        // every dependency refer to a real step — the executor assumes topological
        // ordering, so a cycle or dangling reference deadlocks the run.
        if (plan.Steps.Count == 0)
        {
            return;
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId))
            {
                throw new GeoprocessingValidationException("Every step requires a non-empty stepId.");
            }

            if (!stepIds.Add(step.StepId))
            {
                throw new GeoprocessingValidationException(
                    $"Duplicate step identifier '{step.StepId}'.");
            }
        }

        foreach (var step in plan.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!stepIds.Contains(dep))
                {
                    throw new GeoprocessingValidationException(
                        $"Step '{step.StepId}' depends on unknown step '{dep}'.");
                }

                if (string.Equals(dep, step.StepId, StringComparison.Ordinal))
                {
                    throw new GeoprocessingValidationException(
                        $"Step '{step.StepId}' cannot depend on itself.");
                }
            }
        }

        // Kahn's algorithm: iteratively remove nodes with zero in-degree; if any remain,
        // the remaining subgraph contains a cycle.
        var inDegree = plan.Steps.ToDictionary(s => s.StepId, _ => 0, StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            edges[step.StepId] = new List<string>();
        }

        foreach (var step in plan.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                edges[dep].Add(step.StepId);
                inDegree[step.StepId]++;
            }
        }

        var ready = new Queue<string>(inDegree.Where(p => p.Value == 0).Select(p => p.Key));
        var visited = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            visited++;
            // Not a .Where(...) candidate: the condition (--inDegree[next]) has the
            // side effect of decrementing in-degree, which Kahn's algorithm requires
            // for every edge regardless of the branch taken.
            foreach (var next in (edges[current]).Where(next => --inDegree[next] == 0))
            {
                ready.Enqueue(next);
            }
        }

        if (visited != plan.Steps.Count)
        {
            throw new GeoprocessingValidationException(
                "Plan step dependency graph contains a cycle.");
        }
    }

    public static IReadOnlyList<AnalysisPlanStep> TopologicalOrder(AnalysisPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Steps.Count <= 1)
        {
            return plan.Steps;
        }

        var byId = plan.Steps.ToDictionary(s => s.StepId, StringComparer.Ordinal);
        var inDegree = plan.Steps.ToDictionary(s => s.StepId, _ => 0, StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            edges[step.StepId] = new List<string>();
        }

        foreach (var step in plan.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                edges[dep].Add(step.StepId);
                inDegree[step.StepId]++;
            }
        }

        // Deterministic Kahn: a min-ordered ready set keyed by stepId breaks ties so
        // independent steps always emit in the same (ordinal) order.
        var ready = new SortedSet<string>(
            inDegree.Where(p => p.Value == 0).Select(p => p.Key),
            StringComparer.Ordinal);

        var ordered = new List<AnalysisPlanStep>(plan.Steps.Count);
        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            ordered.Add(byId[current]);
            // Not a .Where(...) candidate: see the topological-sort loop above — the
            // condition decrements shared in-degree state as a side effect.
            foreach (var next in (edges[current]).Where(next => --inDegree[next] == 0))
            {
                ready.Add(next);
            }
        }

        // A cycle would leave steps unvisited; fall back to declared order rather
        // than dropping steps (Validate is expected to have rejected cycles already).
        return ordered.Count == plan.Steps.Count ? ordered : plan.Steps;
    }
}
