// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

public static class WorkflowStepExecutionValidator
{
    public static IReadOnlyDictionary<string, string> Validate(WorkflowStepExecutionOptions options)
    {
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Enum.IsDefined(options.FailurePolicy))
        {
            failures["failurePolicy"] = "Failure policy must be Fail or Skip.";
        }

        if (options.TimeoutSeconds is <= 0)
        {
            failures["timeoutSeconds"] = "Timeout must be a positive number of seconds.";
        }
        ValidateRetry(options.RetryPolicy, failures);
        ValidateCondition(options.Condition, failures);
        ValidateForEach(options.ForEach, failures);
        return failures;
    }

    private static void ValidateRetry(StepRetryPolicy? policy, Dictionary<string, string> failures)
    {
        if (policy is { } retry)
        {
            if (retry.MaxAttempts < 1)
            {
                failures["retryPolicy.maxAttempts"] = "Retry policy must allow at least one attempt.";
            }

            if (retry.InitialDelaySeconds < 0)
            {
                failures["retryPolicy.initialDelaySeconds"] = "Retry initial delay must be non-negative.";
            }

            if (!double.IsFinite(retry.BackoffMultiplier) || retry.BackoffMultiplier < 1)
            {
                failures["retryPolicy.backoffMultiplier"] = "Retry backoff multiplier must be finite and at least 1.";
            }

            if (retry.MaxDelaySeconds < 1)
            {
                failures["retryPolicy.maxDelaySeconds"] = "Retry maximum delay must be positive.";
            }
        }
    }

    private static void ValidateCondition(WorkflowStepCondition? condition, Dictionary<string, string> failures)
    {
        if (condition is not null)
        {
            if (!Enum.IsDefined(condition.Kind))
            {
                failures["condition.kind"] = "Condition kind is not supported.";
            }

            if (condition.Threshold < 0)
            {
                failures["condition.threshold"] = "Condition threshold must be non-negative.";
            }
        }
    }

    private static void ValidateForEach(WorkflowForEachSpec? forEach, Dictionary<string, string> failures)
    {
        if (forEach is not null)
        {
            if (forEach.Items is null || forEach.Items.Count == 0)
            {
                failures["forEach.items"] = "ForEach must declare at least one item.";
            }
            else if (forEach.Items.Count > WorkflowForEachSpec.HardIterationLimit || forEach.Items.Any(item => item is null))
            {
                failures["forEach.items"] = $"ForEach requires non-null items within the hard limit of {WorkflowForEachSpec.HardIterationLimit}.";
            }

            if (forEach.MaxIterations is { } max && (max < 1 || forEach.Items?.Count > max))
            {
                failures["forEach.maxIterations"] = $"ForEach declares {forEach.Items?.Count ?? 0} items but MaxIterations is {max}; it must be at least 1 and cover every declared item.";
            }

            if (string.IsNullOrEmpty(forEach.ItemPlaceholder))
            {
                failures["forEach.itemPlaceholder"] = "ForEach item placeholder must be non-empty.";
            }
        }
    }

    public static IReadOnlyDictionary<string, string> ValidateReferences(
        WorkflowStepExecutionOptions options,
        IReadOnlyList<string> dependencies,
        (IReadOnlySet<string> StepIds, IReadOnlySet<string> ForEachStepIds) topology)
    {
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.Condition is { } condition)
        {
            if (string.IsNullOrWhiteSpace(condition.SourceStepId) || !topology.StepIds.Contains(condition.SourceStepId))
            {
                failures["condition.sourceStepId"] = $"Condition references unknown source step '{condition.SourceStepId}'.";
            }
            else if (!dependencies.Contains(condition.SourceStepId, StringComparer.Ordinal))
            {
                failures["condition.sourceStepId"] = $"Condition references source step '{condition.SourceStepId}' which is not declared in DependsOn.";
            }
            else if (topology.ForEachStepIds.Contains(condition.SourceStepId))
            {
                failures["condition.sourceStepId"] = $"Condition may not read from ForEach step '{condition.SourceStepId}'.";
            }
        }

        if (options.ForEach is not null && dependencies.Any(topology.ForEachStepIds.Contains))
        {
            failures["forEach"] = "A ForEach step may not depend on a ForEach step; nested iteration is not supported.";
        }

        return failures;
    }
}
