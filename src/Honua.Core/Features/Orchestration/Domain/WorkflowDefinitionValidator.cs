// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Validates the structural integrity of a <see cref="WorkflowDefinition"/>.
/// </summary>
public static class WorkflowDefinitionValidator
{
    /// <summary>
    /// Validates the definition and returns a list of failure messages. An empty list indicates success.
    /// </summary>
    public static IReadOnlyList<string> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(definition.WorkflowId))
        {
            failures.Add("Workflow identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            failures.Add("Workflow name is required.");
        }

        if (definition.Steps.Count == 0)
        {
            failures.Add("Workflow must contain at least one step.");
            return failures;
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId))
            {
                failures.Add("Every step must declare a non-empty step identifier.");
                continue;
            }

            if (!stepIds.Add(step.StepId))
            {
                failures.Add($"Duplicate step identifier '{step.StepId}'.");
            }

            if (step.Plan is null)
            {
                failures.Add($"Step '{step.StepId}' must declare a canonical analysis plan.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(step.Plan.PlanId))
                {
                    failures.Add($"Step '{step.StepId}' plan must declare a plan identifier.");
                }

                if (step.Plan.Steps.Count == 0)
                {
                    failures.Add($"Step '{step.StepId}' plan must contain at least one analysis step.");
                }
            }

            if (step.RetryPolicy != null && step.RetryPolicy.MaxAttempts < 1)
            {
                failures.Add($"Step '{step.StepId}' retry policy must allow at least one attempt.");
            }
        }

        foreach (var step in definition.Steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!stepIds.Contains(dependency))
                {
                    failures.Add($"Step '{step.StepId}' depends on unknown step '{dependency}'.");
                }
            }

            foreach (var binding in step.InputBindings)
            {
                if (string.IsNullOrWhiteSpace(binding.SourceStepId) ||
                    !stepIds.Contains(binding.SourceStepId))
                {
                    failures.Add($"Step '{step.StepId}' binding references unknown source step '{binding.SourceStepId}'.");
                }
                else if (!step.DependsOn.Contains(binding.SourceStepId, StringComparer.Ordinal))
                {
                    failures.Add($"Step '{step.StepId}' binding references source step '{binding.SourceStepId}' which is not declared in DependsOn. Binding sources must be explicit dependencies so execution ordering and cycle detection cover all data edges.");
                }

                if (string.IsNullOrWhiteSpace(binding.TargetInputKey))
                {
                    failures.Add($"Step '{step.StepId}' binding is missing a target input key.");
                }

                if (string.IsNullOrWhiteSpace(binding.SourceArtifactSelector))
                {
                    failures.Add($"Step '{step.StepId}' binding is missing an artifact selector.");
                }
            }
        }

        // Cycle detection requires unique step identifiers; skip when duplicates were flagged
        // above so the graph traversal does not throw on a non-unique lookup.
        if (stepIds.Count == definition.Steps.Count && HasCycle(definition.Steps))
        {
            failures.Add("Workflow contains a dependency cycle.");
        }

        if (definition.Trigger is { Kind: WorkflowTriggerKind.Cron } trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger.CronExpression))
            {
                failures.Add("Cron trigger requires a non-empty cron expression.");
            }
            else
            {
                try
                {
                    _ = CronExpression.Parse(trigger.CronExpression);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    failures.Add($"Cron trigger expression '{trigger.CronExpression}' is not a valid 5-field cron expression: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(trigger.TimeZone))
            {
                try
                {
                    _ = TimeZoneInfo.FindSystemTimeZoneById(trigger.TimeZone);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    failures.Add($"Cron trigger time zone '{trigger.TimeZone}' could not be resolved: {ex.Message}");
                }
            }
        }

        return failures;
    }

    private static bool HasCycle(IReadOnlyList<WorkflowStepDefinition> steps)
    {
        var lookup = steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        var unvisited = new HashSet<string>(lookup.Keys, StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (unvisited.Contains(step.StepId) && Visit(step.StepId, lookup, unvisited, active))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Visit(
        string stepId,
        IReadOnlyDictionary<string, WorkflowStepDefinition> lookup,
        HashSet<string> unvisited,
        HashSet<string> active)
    {
        if (!active.Add(stepId))
        {
            return true;
        }

        if (lookup.TryGetValue(stepId, out var step))
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!lookup.ContainsKey(dependency))
                {
                    continue;
                }

                if (Visit(dependency, lookup, unvisited, active))
                {
                    return true;
                }
            }
        }

        active.Remove(stepId);
        unvisited.Remove(stepId);
        return false;
    }
}
