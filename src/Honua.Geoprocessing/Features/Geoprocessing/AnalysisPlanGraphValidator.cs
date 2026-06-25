// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Structural (dependency-graph) validation for an <see cref="AnalysisPlan"/>.
/// Enforces that every step has a unique, non-empty identifier, that each
/// declared dependency refers to a real step (no dangling references and no
/// self-dependencies), and that the resulting dependency graph is acyclic.
///
/// <para>
/// The executor assumes a topological ordering of steps, so a cycle or a
/// dangling reference would deadlock (or misorder) the run; this validator is
/// the single fail-fast gate that rejects such plans before submission. It is
/// shared by the durable submit/validate/dry-run paths
/// (<see cref="GeoprocessingJobService"/>) and the headless GP Devkit
/// <c>honua gp plan</c> dry-run path so both reject the exact same malformed
/// graphs with the exact same message.
/// </para>
/// </summary>
internal static class AnalysisPlanGraphValidator
{
    /// <summary>
    /// Validates the plan's step dependency graph, throwing
    /// <see cref="GeoprocessingValidationException"/> on the first structural
    /// defect (empty/duplicate stepId, dangling or self dependency, or a cycle).
    /// An empty plan is treated as structurally valid here; callers enforce the
    /// "at least one step" rule separately where it applies.
    /// </summary>
    /// <param name="plan">The plan whose step graph is validated.</param>
    /// <exception cref="GeoprocessingValidationException">
    /// Thrown when the step graph is malformed.
    /// </exception>
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
            foreach (var next in edges[current])
            {
                if (--inDegree[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        if (visited != plan.Steps.Count)
        {
            throw new GeoprocessingValidationException(
                "Plan step dependency graph contains a cycle.");
        }
    }

    /// <summary>
    /// Returns the steps of <paramref name="plan"/> in a deterministic topological
    /// order (dependencies before dependents). Ties are broken by ordinal stepId so
    /// the same plan always yields the same execution order in the plan preview.
    /// Assumes <see cref="Validate(AnalysisPlan)"/> has already accepted the plan;
    /// callers that have not validated should call it first.
    /// </summary>
    /// <param name="plan">The (already structurally valid) plan to order.</param>
    /// <returns>The plan's steps in topological execution order.</returns>
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
            foreach (var next in edges[current])
            {
                if (--inDegree[next] == 0)
                {
                    ready.Add(next);
                }
            }
        }

        // A cycle would leave steps unvisited; fall back to declared order rather
        // than dropping steps (Validate is expected to have rejected cycles already).
        return ordered.Count == plan.Steps.Count ? ordered : plan.Steps;
    }
}
