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

    [Fact]
    public async Task TickAsync_PersistsDurableCursorAfterFiring()
    {
        // After a fire, the durable cursor should advance past the fired occurrence so that a
        // fresh scheduler (post-restart, in-memory cache lost) does not replay it.
        var harness = BuildHarness(new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero));
        var definition = BuildCronDefinition(harness.Clock.GetUtcNow(), "* * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Scheduler.TickAsync(CancellationToken.None);

        var cursor = await harness.Definitions.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursor);
        Assert.True(cursor > definition.UpdatedAt);
    }

    [Fact]
    public async Task TickAsync_RespectsDurableCursor_AfterRestart()
    {
        // Simulate: scheduler fires once, process restarts (in-memory cursor lost) inside the
        // same cron window. Without a durable cursor, a fresh scheduler would enumerate from
        // UpdatedAt and replay the already-fired occurrence. The cursor keeps the replacement
        // from re-firing it.
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var definitionStore = new FakeWorkflowDefinitionStore();
        var runStore = new FakeWorkflowRunStore();
        var progressStore = new FakeProgressStore();
        var jobService = new FakeWorkflowJobExecutor();
        var clock = new TestClock(start);
        var definition = BuildCronDefinition(start, "* * * * *");
        await definitionStore.TryCreateAsync(definition);

        var engineA = new WorkflowOrchestrationEngine(
            runStore, definitionStore, jobService, progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var schedulerA = new WorkflowSchedulerBackgroundService(
            definitionStore, engineA, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        // Advance into the first cron window but not the second: only 12:01:00 is eligible.
        clock.Advance(TimeSpan.FromSeconds(80));
        await schedulerA.TickAsync(CancellationToken.None);
        var firstBatch = await runStore.ListActiveAsync();
        Assert.Single(firstBatch);
        var firedAt = firstBatch[0].Metadata["scheduler.fire_time"];
        Assert.Equal(
            start.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            firedAt);

        // "Restart": new engine, new scheduler (in-memory cursor state lost). We stay inside
        // the same cron window so any replay is visible as a duplicate run.
        var engineB = new WorkflowOrchestrationEngine(
            runStore, definitionStore, jobService, progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var schedulerB = new WorkflowSchedulerBackgroundService(
            definitionStore, engineB, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        await schedulerB.TickAsync(CancellationToken.None);

        Assert.Single(runStore.Snapshot);

        // Advance past the next cron boundary and scheduler B should fire exactly once for
        // the new 12:02:00 occurrence without replaying 12:01:00.
        clock.Advance(TimeSpan.FromMinutes(1));
        await schedulerB.TickAsync(CancellationToken.None);

        Assert.Equal(2, runStore.Snapshot.Count);
        var fireTimes = runStore.Snapshot
            .Select(r => r.Metadata["scheduler.fire_time"])
            .ToArray();
        Assert.Contains(firedAt, fireTimes);
        Assert.Equal(fireTimes.Length, fireTimes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TickAsync_SeedsCursorFromDurableState_SkippingBacklog()
    {
        // Boot state: the durable cursor already reflects a prior firing. The scheduler must
        // not enumerate occurrences earlier than the cursor, otherwise cold-start replicas
        // would create duplicate runs for any occurrence inside the backlog window.
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        var definition = BuildCronDefinition(start, "* * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        // Durable cursor claims 12:05 as the last fired occurrence.
        harness.Definitions.SeedScheduleCursor(definition.WorkflowId, start.AddMinutes(5));

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Scheduler.TickAsync(CancellationToken.None);

        var runs = await harness.RunStore.ListActiveAsync();
        Assert.Single(runs);
        // Only the 12:06 occurrence should fire; the backlog (12:01..12:05) is skipped.
        Assert.Equal(
            start.AddMinutes(6).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            runs[0].Metadata["scheduler.fire_time"]);
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
