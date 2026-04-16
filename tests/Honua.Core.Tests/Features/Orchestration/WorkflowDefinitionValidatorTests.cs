// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Tests.Features.Orchestration;

public sealed class WorkflowDefinitionValidatorTests
{
    private static readonly string[] Empty = [];
    private static readonly string[] DependsOnA = ["a"];
    private static readonly string[] DependsOnB = ["b"];
    private static readonly string[] DependsOnMissing = ["missing"];

    [Fact]
    public void Validate_ReturnsNoFailures_ForSimpleDag()
    {
        var definition = BuildDefinition(("a", Empty), ("b", DependsOnA));

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_DetectsUnknownDependency()
    {
        var definition = BuildDefinition(("a", DependsOnMissing));

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("unknown step 'missing'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DetectsDuplicateStepIds()
    {
        var definition = BuildDefinition(("a", Empty), ("a", Empty));

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("Duplicate step identifier 'a'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DetectsDependencyCycle()
    {
        var definition = BuildDefinition(("a", DependsOnB), ("b", DependsOnA));

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("dependency cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiresCronExpression_WhenCronTriggerSet()
    {
        var definition = BuildDefinition(("a", Empty)) with
        {
            Trigger = new WorkflowTrigger { Kind = WorkflowTriggerKind.Cron, CronExpression = "  " }
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("Cron trigger requires", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DetectsMissingBindingSourceStep()
    {
        var definition = BuildDefinition(("a", Empty));
        var binding = new StepInputBinding
        {
            SourceStepId = "missing",
            SourceArtifactSelector = "artifact:0",
            TargetInputKey = "in"
        };
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with
                {
                    InputBindings = [binding]
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("unknown source step 'missing'", StringComparison.Ordinal));
    }

    private static WorkflowDefinition BuildDefinition(params (string StepId, string[] DependsOn)[] steps)
    {
        var now = DateTimeOffset.UtcNow;
        var workflowSteps = steps.Select(s =>
        {
            var planStep = new AnalysisPlanStep
            {
                StepId = $"ap-{s.StepId}",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "noop"
            };
            return new WorkflowStepDefinition
            {
                StepId = s.StepId,
                DependsOn = s.DependsOn,
                Plan = new AnalysisPlan
                {
                    PlanId = $"plan-{s.StepId}",
                    IntentId = $"intent-{s.StepId}",
                    Steps = [planStep]
                }
            };
        }).ToArray();

        return new WorkflowDefinition
        {
            WorkflowId = "wf-test",
            Name = "Test workflow",
            Steps = workflowSteps,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
