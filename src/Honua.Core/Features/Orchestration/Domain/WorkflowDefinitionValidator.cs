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

            if (!string.IsNullOrWhiteSpace(step.StepId) &&
                step.StepId.Contains(WorkflowDefinitionExpander.IterationSeparator, StringComparison.Ordinal))
            {
                failures.Add($"Step '{step.StepId}' identifier must not contain the reserved iteration separator '{WorkflowDefinitionExpander.IterationSeparator}'.");
            }

            ValidateForEach(step, failures);
        }

        var forEachStepIds = new HashSet<string>(
            definition.Steps
                .Where(s => s.ForEach is not null && !string.IsNullOrWhiteSpace(s.StepId))
                .Select(s => s.StepId),
            StringComparer.Ordinal);

        foreach (var step in definition.Steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!stepIds.Contains(dependency))
                {
                    failures.Add($"Step '{step.StepId}' depends on unknown step '{dependency}'.");
                }
                else if (step.ForEach is not null && forEachStepIds.Contains(dependency))
                {
                    failures.Add($"ForEach step '{step.StepId}' may not depend on ForEach step '{dependency}'; nested iteration is not supported.");
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
                else if (forEachStepIds.Contains(binding.SourceStepId))
                {
                    failures.Add($"Step '{step.StepId}' binding may not read from ForEach step '{binding.SourceStepId}'; binding from an iteration's aggregate output is not supported.");
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

            ValidateCondition(step, stepIds, forEachStepIds, failures);
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

        if (definition.Trigger is { Kind: WorkflowTriggerKind.ChangeFeed } changeFeed)
        {
            if (changeFeed.WatchedLayerIds.Count == 0)
            {
                failures.Add("Change-feed trigger requires at least one watched layer id.");
            }
            else if (changeFeed.WatchedLayerIds.Any(id => id < 0))
            {
                failures.Add("Change-feed trigger watched layer ids must be non-negative.");
            }
        }

        if (definition.Trigger is { Kind: WorkflowTriggerKind.ObjectStore } objectStore)
        {
            if (objectStore.ObjectStore is null)
            {
                failures.Add("Object-store trigger requires an object-store configuration.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(objectStore.ObjectStore.StoreId))
                {
                    failures.Add("Object-store trigger requires a non-empty store id.");
                }

                if (objectStore.ObjectStore.PollInterval is { } interval && interval <= TimeSpan.Zero)
                {
                    failures.Add("Object-store trigger poll interval must be positive when specified.");
                }
            }
        }

        return failures;
    }

    private static void ValidateForEach(WorkflowStepDefinition step, List<string> failures)
    {
        if (step.ForEach is not { } forEach)
        {
            return;
        }

        if (forEach.Items.Count == 0)
        {
            failures.Add($"Step '{step.StepId}' ForEach must declare at least one item.");
        }

        if (forEach.Items.Count > WorkflowForEachSpec.HardIterationLimit)
        {
            failures.Add($"Step '{step.StepId}' ForEach declares {forEach.Items.Count} items, exceeding the hard limit of {WorkflowForEachSpec.HardIterationLimit}.");
        }

        if (forEach.MaxIterations is { } max)
        {
            if (max < 1)
            {
                failures.Add($"Step '{step.StepId}' ForEach MaxIterations must be at least 1.");
            }
            else if (forEach.Items.Count > max)
            {
                failures.Add($"Step '{step.StepId}' ForEach declares {forEach.Items.Count} items but MaxIterations is {max}.");
            }
        }

        if (string.IsNullOrEmpty(forEach.ItemPlaceholder))
        {
            failures.Add($"Step '{step.StepId}' ForEach item placeholder must be non-empty.");
        }
    }

    private static void ValidateCondition(
        WorkflowStepDefinition step,
        HashSet<string> stepIds,
        HashSet<string> forEachStepIds,
        List<string> failures)
    {
        if (step.Condition is not { } condition)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(condition.SourceStepId) || !stepIds.Contains(condition.SourceStepId))
        {
            failures.Add($"Step '{step.StepId}' condition references unknown source step '{condition.SourceStepId}'.");
        }
        else
        {
            if (!step.DependsOn.Contains(condition.SourceStepId, StringComparer.Ordinal))
            {
                failures.Add($"Step '{step.StepId}' condition references source step '{condition.SourceStepId}' which is not declared in DependsOn. Condition sources must be explicit dependencies so they are terminal before the branch is evaluated.");
            }

            if (forEachStepIds.Contains(condition.SourceStepId))
            {
                failures.Add($"Step '{step.StepId}' condition may not read from ForEach step '{condition.SourceStepId}'.");
            }
        }

        if (condition.Threshold < 0)
        {
            failures.Add($"Step '{step.StepId}' condition threshold must be non-negative.");
        }
    }

    private static bool HasCycle(IReadOnlyList<WorkflowStepDefinition> steps)
    {
        var lookup = steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        var unvisited = new HashSet<string>(lookup.Keys, StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        return steps.Any(step => unvisited.Contains(step.StepId) && Visit(step.StepId, lookup, unvisited, active));
    }

    private static bool Visit(
        string stepId,
        Dictionary<string, WorkflowStepDefinition> lookup,
        HashSet<string> unvisited,
        HashSet<string> active)
    {
        // Iterative DFS with an explicit stack: workflow definitions come from external
        // requests, and a recursive visit would overflow the call stack on a dependency
        // chain thousands of steps deep (StackOverflowException is not catchable).
        // Frames with Exit=true unwind a fully explored step.
        var stack = new Stack<(string StepId, bool Exit)>();
        stack.Push((stepId, false));

        while (stack.Count > 0)
        {
            var (current, exit) = stack.Pop();

            if (exit)
            {
                active.Remove(current);
                unvisited.Remove(current);
                continue;
            }

            if (!unvisited.Contains(current))
            {
                // Already fully explored via another dependency path; no cycle through this step.
                continue;
            }

            if (!active.Add(current))
            {
                return true;
            }

            stack.Push((current, true));

            if (lookup.TryGetValue(current, out var step))
            {
                foreach (var dependency in step.DependsOn)
                {
                    if (!lookup.ContainsKey(dependency))
                    {
                        continue;
                    }

                    if (active.Contains(dependency))
                    {
                        // Back edge to a step on the current path: dependency cycle.
                        return true;
                    }

                    if (unvisited.Contains(dependency))
                    {
                        stack.Push((dependency, false));
                    }
                }
            }
        }

        return false;
    }
}
