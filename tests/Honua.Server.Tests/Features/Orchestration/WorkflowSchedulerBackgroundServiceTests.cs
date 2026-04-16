// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class WorkflowSchedulerBackgroundServiceTests
{
    [Fact]
    public async Task TickAsync_CreatesRun_WhenCronFireTimeElapsed()
    {
        // Start at 12:00:00; trigger fires every minute, so next occurrence after the
        // definition's UpdatedAt (12:00:00) is 12:01:00. After we advance past that point
        // the scheduler should create a run.
        var harness = BuildHarness(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var definition = BuildCronDefinition(harness.Clock.GetUtcNow(), "* * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));

        await harness.Scheduler.TickAsync(CancellationToken.None);

        var runs = await harness.RunStore.ListActiveAsync();
        Assert.Single(runs);
        Assert.Equal(WorkflowTriggerKind.Cron, runs[0].TriggerKind);
        Assert.True(runs[0].Metadata.ContainsKey("scheduler.fire_time"));
    }

    [Fact]
    public async Task TickAsync_DoesNotCreateRun_BeforeFireTime()
    {
        var harness = BuildHarness(new DateTimeOffset(2026, 4, 16, 12, 0, 30, TimeSpan.Zero));
        var definition = BuildCronDefinition(harness.Clock.GetUtcNow(), "0 * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        await harness.Scheduler.TickAsync(CancellationToken.None);

        var runs = await harness.RunStore.ListActiveAsync();
        Assert.Empty(runs);
    }

    [Fact]
    public async Task TickAsync_DoesNotCreateDuplicateRuns_ForSameFiring()
    {
        // Use an hourly cron so a single advanced window contains exactly one occurrence.
        // Two ticks within that same window must create only one run.
        var harness = BuildHarness(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var definition = BuildCronDefinition(harness.Clock.GetUtcNow(), "0 * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(65));

        await harness.Scheduler.TickAsync(CancellationToken.None);
        await harness.Scheduler.TickAsync(CancellationToken.None);

        var runs = await harness.RunStore.ListActiveAsync();
        Assert.Single(runs);
    }

    [Fact]
    public async Task TickAsync_SkipsDefinitionsWithDisabledTrigger()
    {
        var harness = BuildHarness(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var definition = BuildCronDefinition(harness.Clock.GetUtcNow(), "* * * * *") with
        {
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Cron,
                CronExpression = "* * * * *",
                Enabled = false
            }
        };
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Scheduler.TickAsync(CancellationToken.None);

        var runs = await harness.RunStore.ListActiveAsync();
        Assert.Empty(runs);
    }

    private static SchedulerHarness BuildHarness(DateTimeOffset start) => new(start);

    private static WorkflowDefinition BuildCronDefinition(DateTimeOffset now, string cron)
        => new()
        {
            WorkflowId = "wf-cron",
            Name = "scheduled",
            Steps = new[]
            {
                new WorkflowStepDefinition
                {
                    StepId = "a",
                    Plan = new AnalysisPlan
                    {
                        PlanId = "plan-a",
                        IntentId = "intent-a",
                        Steps = new[]
                        {
                            new AnalysisPlanStep
                            {
                                StepId = "ap",
                                Kind = AnalysisPlanStepKind.Geoprocess,
                                ProcessId = "noop"
                            }
                        }
                    }
                }
            },
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Cron,
                CronExpression = cron,
                Enabled = true
            },
            CreatedAt = now,
            UpdatedAt = now
        };

    private sealed class SchedulerHarness
    {
        public SchedulerHarness(DateTimeOffset start)
        {
            RunStore = new FakeWorkflowRunStore();
            Definitions = new FakeWorkflowDefinitionStore();
            Progress = new FakeProgressStore();
            JobService = new FakeWorkflowJobExecutor();
            Clock = new TestClock(start);
            Engine = new WorkflowOrchestrationEngine(
                RunStore,
                Definitions,
                JobService,
                Progress,
                Clock,
                NullLogger<WorkflowOrchestrationEngine>.Instance);
            Scheduler = new WorkflowSchedulerBackgroundService(
                Definitions,
                Engine,
                Clock,
                NullLogger<WorkflowSchedulerBackgroundService>.Instance);
        }

        public FakeWorkflowRunStore RunStore { get; }
        public FakeWorkflowDefinitionStore Definitions { get; }
        public FakeProgressStore Progress { get; }
        public FakeWorkflowJobExecutor JobService { get; }
        public TestClock Clock { get; }
        public WorkflowOrchestrationEngine Engine { get; }
        public WorkflowSchedulerBackgroundService Scheduler { get; }
    }
}
