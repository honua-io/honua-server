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
    public void Validate_RejectsMalformedCronExpression()
    {
        var definition = BuildDefinition(("a", Empty)) with
        {
            Trigger = new WorkflowTrigger { Kind = WorkflowTriggerKind.Cron, CronExpression = "nope nope nope" }
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("not a valid 5-field cron expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsUnknownTimeZone()
    {
        var definition = BuildDefinition(("a", Empty)) with
        {
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Cron,
                CronExpression = "*/5 * * * *",
                TimeZone = "Mars/Olympus_Mons"
            }
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("could not be resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsWellFormedCronTrigger()
    {
        var definition = BuildDefinition(("a", Empty)) with
        {
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Cron,
                CronExpression = "*/5 * * * *",
                TimeZone = TimeZoneInfo.Utc.Id
            }
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(failures);
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

    [Fact]
    public void Validate_RejectsBindingSource_NotInDependsOn()
    {
        var definition = BuildDefinition(("a", Empty), ("b", Empty));
        var binding = new StepInputBinding
        {
            SourceStepId = "a",
            SourceArtifactSelector = "artifact:0",
            TargetInputKey = "in"
        };
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0],
                definition.Steps[1] with
                {
                    InputBindings = [binding]
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("not declared in DependsOn", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsBinding_WhenSourceIsInDependsOn()
    {
        var definition = BuildDefinition(("a", Empty), ("b", DependsOnA));
        var binding = new StepInputBinding
        {
            SourceStepId = "a",
            SourceArtifactSelector = "artifact:0",
            TargetInputKey = "in"
        };
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0],
                definition.Steps[1] with
                {
                    InputBindings = [binding]
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_DetectsCycleCreatedByBindingDependency()
    {
        // a depends on b, b has a binding on a (which now requires a→DependsOn).
        // If the user adds a to b's DependsOn to satisfy the binding rule,
        // the cycle a→b→a must be caught. This verifies that binding edges
        // cannot bypass cycle detection.
        var definition = BuildDefinition(("a", DependsOnB), ("b", DependsOnA));
        var binding = new StepInputBinding
        {
            SourceStepId = "a",
            SourceArtifactSelector = "artifact:0",
            TargetInputKey = "in"
        };
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0],
                definition.Steps[1] with
                {
                    InputBindings = [binding]
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("dependency cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsConditionalBranch_WhenSourceInDependsOn()
    {
        var definition = BuildDefinition(("a", Empty), ("b", DependsOnA));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0],
                definition.Steps[1] with
                {
                    Condition = new WorkflowStepCondition("a", WorkflowStepConditionKind.UpstreamSucceeded)
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_RejectsCondition_WhenSourceNotInDependsOn()
    {
        var definition = BuildDefinition(("a", Empty), ("b", Empty));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0],
                definition.Steps[1] with
                {
                    Condition = new WorkflowStepCondition("a", WorkflowStepConditionKind.UpstreamSucceeded)
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("condition references source step 'a' which is not declared in DependsOn", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsCondition_WithUnknownSourceStep()
    {
        var definition = BuildDefinition(("a", Empty));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with
                {
                    Condition = new WorkflowStepCondition("ghost", WorkflowStepConditionKind.UpstreamSucceeded)
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("condition references unknown source step 'ghost'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsWellFormedForEach()
    {
        var definition = BuildDefinition(("a", Empty));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with
                {
                    ForEach = new WorkflowForEachSpec(["x", "y"], MaxIterations: 5)
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_RejectsForEach_WithNoItems()
    {
        var definition = BuildDefinition(("a", Empty));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with
                {
                    ForEach = new WorkflowForEachSpec([])
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("ForEach must declare at least one item", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsForEach_ExceedingMaxIterations()
    {
        var definition = BuildDefinition(("a", Empty));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with
                {
                    ForEach = new WorkflowForEachSpec(["x", "y", "z"], MaxIterations: 2)
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("MaxIterations is 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsStepId_ContainingIterationSeparator()
    {
        var definition = BuildDefinition(("a::0", Empty));

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("reserved iteration separator", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsBinding_FromForEachStep()
    {
        var definition = BuildDefinition(("a", Empty), ("b", DependsOnA));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with { ForEach = new WorkflowForEachSpec(["x", "y"]) },
                definition.Steps[1] with
                {
                    InputBindings =
                    [
                        new StepInputBinding
                        {
                            SourceStepId = "a",
                            SourceArtifactSelector = "artifact:0",
                            TargetInputKey = "in"
                        }
                    ]
                }
            ]
        };

        var failures = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(failures, f => f.Contains("may not read from ForEach step 'a'", StringComparison.Ordinal));
    }

    [Fact]
    public void Expand_UnrollsForEachIntoPerItemSteps_WithSubstitution()
    {
        var definition = BuildDefinition(("a", Empty));
        var planStep = new AnalysisPlanStep
        {
            StepId = "ap",
            Kind = AnalysisPlanStepKind.Geoprocess,
            ProcessId = "noop",
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["region"] = "${item}" }
        };
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with
                {
                    Plan = new AnalysisPlan { PlanId = "p", IntentId = "i", Steps = [planStep] },
                    ForEach = new WorkflowForEachSpec(["east", "west"])
                }
            ]
        };

        var expanded = WorkflowDefinitionExpander.Expand(definition);

        Assert.Equal(new[] { "a::0", "a::1" }, expanded.Steps.Select(s => s.StepId).ToArray());
        Assert.All(expanded.Steps, s => Assert.Null(s.ForEach));
        Assert.Equal("east", expanded.Steps[0].Plan.Steps[0].Inputs["region"]);
        Assert.Equal("west", expanded.Steps[1].Plan.Steps[0].Inputs["region"]);
    }

    [Fact]
    public void Expand_FansInDependentsOnForEachStep()
    {
        var definition = BuildDefinition(("a", Empty), ("b", DependsOnA));
        definition = definition with
        {
            Steps =
            [
                definition.Steps[0] with { ForEach = new WorkflowForEachSpec(["x", "y", "z"]) },
                definition.Steps[1]
            ]
        };

        var expanded = WorkflowDefinitionExpander.Expand(definition);

        var b = expanded.Steps.Single(s => s.StepId == "b");
        Assert.Equal(new[] { "a::0", "a::1", "a::2" }, b.DependsOn.ToArray());
    }

    [Fact]
    public void Expand_IsDeterministic_AcrossRepeatedCalls()
    {
        var definition = BuildDefinition(("a", Empty));
        definition = definition with
        {
            Steps = [definition.Steps[0] with { ForEach = new WorkflowForEachSpec(["x", "y"]) }]
        };

        var first = WorkflowDefinitionExpander.Expand(definition);
        var second = WorkflowDefinitionExpander.Expand(definition);

        Assert.Equal(
            first.Steps.Select(s => s.StepId).ToArray(),
            second.Steps.Select(s => s.StepId).ToArray());
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
