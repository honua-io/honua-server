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
    public async Task TickAsync_RetriesSameOccurrence_WhenCreateRunAsyncFailsTransiently()
    {
        // Regression: previously the tick advanced both the in-memory and durable
        // cursors before calling CreateRunAsync. A transient failure inside
        // CreateRunAsync (e.g. a run-store blip) would then permanently skip the
        // occurrence. After the fix the winner holds the cursor at the failed
        // occurrence, releases the claim, and the next tick can retry.
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        var definition = BuildCronDefinition(start, "* * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        // Advance into the first cron window (12:01 is eligible).
        harness.Clock.Advance(TimeSpan.FromMinutes(2));

        // Inject a transient store failure for the first CreateRunAsync call.
        harness.RunStore.NextTryCreateFailure = new InvalidOperationException("transient store blip");

        await harness.Scheduler.TickAsync(CancellationToken.None);

        // No run was created, the durable cursor must not have advanced past the
        // failed occurrence, and no run landed in the run store.
        Assert.Empty(harness.RunStore.Snapshot);
        var cursorAfterTransient = await harness.Definitions.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.True(cursorAfterTransient is null || cursorAfterTransient < start.AddMinutes(1));

        // The claim must have been released so the next tick can retry. The clock
        // hasn't moved, so the same occurrence (12:01) should fire.
        await harness.Scheduler.TickAsync(CancellationToken.None);

        Assert.Single(harness.RunStore.Snapshot);
        var run = harness.RunStore.Snapshot.Single();
        Assert.Equal(
            start.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            run.Metadata["scheduler.fire_time"]);

        var cursorAfterRetry = await harness.Definitions.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursorAfterRetry);
        Assert.True(cursorAfterRetry >= start.AddMinutes(1));
    }

    [Fact]
    public async Task TickAsync_DoesNotInheritStaleCache_WhenDefinitionIsDeletedAndRecreated()
    {
        // MEDIUM regression: after delete+recreate of a workflow id, the scheduler's in-memory
        // _compiled cache held the deleted definition's LastFireAt, and the durable cursor
        // carried the old firing time too. A fresh recreate would therefore skip its first
        // occurrence. The fix clears both state sources on delete, and evicts the compile
        // cache for any workflow id no longer present in the scheduled list.
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        var original = BuildCronDefinition(start, "* * * * *");
        await harness.Definitions.TryCreateAsync(original);

        // Fire the first occurrence so both caches are populated.
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Scheduler.TickAsync(CancellationToken.None);
        Assert.Single(harness.RunStore.Snapshot);
        Assert.NotNull(await harness.Definitions.GetScheduleCursorAsync(original.WorkflowId));

        // Delete the definition — durable cursor must clear, and the next tick must evict
        // the compile cache for the missing id.
        await harness.Definitions.DeleteAsync(original.WorkflowId);
        Assert.Null(await harness.Definitions.GetScheduleCursorAsync(original.WorkflowId));
        await harness.Scheduler.TickAsync(CancellationToken.None);

        // Recreate with the same id and confirm the first occurrence after UpdatedAt fires,
        // rather than inheriting the previous LastFireAt from stale state.
        var recreateAt = harness.Clock.GetUtcNow();
        var replacement = BuildCronDefinition(recreateAt, "* * * * *");
        await harness.Definitions.TryCreateAsync(replacement);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Scheduler.TickAsync(CancellationToken.None);

        // The replacement should produce exactly one additional run — its first occurrence
        // after UpdatedAt — without skipping it because of inherited scheduler state.
        Assert.Equal(2, harness.RunStore.Snapshot.Count);
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

    [Fact]
    public async Task TickAsync_LosingReplicaJumpsToDurableCursor_WhenMultipleOccurrencesBehind()
    {
        // Exercises the !claimed path fix: when a replica's in-memory cursor is far behind
        // the durable cursor, it should jump directly to the durable position instead of
        // crawling forward one occurrence per tick.
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        var definition = BuildCronDefinition(start, "* * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        // Fire the first occurrence so the scheduler's in-memory cursor is initialised.
        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Scheduler.TickAsync(CancellationToken.None);
        Assert.Single(harness.RunStore.Snapshot);

        // Simulate another replica having fired 12:02 through 12:05: pre-claim those
        // fire times and advance the durable cursor as the winner would.
        for (var m = 2; m <= 5; m++)
        {
            await harness.Definitions.TryClaimScheduleFireAsync(
                definition.WorkflowId,
                start.AddMinutes(m),
                TimeSpan.FromHours(1));
        }

        harness.Definitions.SeedScheduleCursor(definition.WorkflowId, start.AddMinutes(5));
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        // The scheduler's in-memory cursor is still at 12:01. Without the fix, each tick
        // would evaluate the next stale occurrence (12:02, 12:03, …), lose the claim, and
        // advance by only one — requiring 4 ticks to catch up. With the fix, the first
        // lost claim jumps to the durable cursor (12:05) and the second tick fires 12:06.
        await harness.Scheduler.TickAsync(CancellationToken.None);
        await harness.Scheduler.TickAsync(CancellationToken.None);

        Assert.Equal(2, harness.RunStore.Snapshot.Count);
        var fireTime = start.AddMinutes(6).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(harness.RunStore.Snapshot, r => r.Metadata["scheduler.fire_time"] == fireTime);
    }

    [Fact]
    public async Task TickAsync_LosingReplicaRevisitsOccurrence_WhenWinnerCrashesBeforeCursorAdvance()
    {
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var definitionStore = new FakeWorkflowDefinitionStore();
        var runStore = new FakeWorkflowRunStore();
        var progressStore = new FakeProgressStore();
        var clock = new TestClock(start);
        var definition = BuildCronDefinition(start, "* * * * *");
        await definitionStore.TryCreateAsync(definition);

        var jobServiceA = new FakeWorkflowJobExecutor();
        var engineA = new WorkflowOrchestrationEngine(
            runStore, definitionStore, jobServiceA, progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var schedulerA = new WorkflowSchedulerBackgroundService(
            definitionStore, engineA, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        clock.Advance(TimeSpan.FromMinutes(2));

        runStore.NextTryCreateFailure = new InvalidOperationException("winner crash");
        await schedulerA.TickAsync(CancellationToken.None);
        Assert.Empty(runStore.Snapshot);

        var cursorAfterCrash = await definitionStore.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.True(cursorAfterCrash is null || cursorAfterCrash < start.AddMinutes(1));

        var jobServiceB = new FakeWorkflowJobExecutor();
        var engineB = new WorkflowOrchestrationEngine(
            runStore, definitionStore, jobServiceB, progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var schedulerB = new WorkflowSchedulerBackgroundService(
            definitionStore, engineB, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        await schedulerB.TickAsync(CancellationToken.None);
        Assert.Single(runStore.Snapshot);
        Assert.Equal(
            start.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            runStore.Snapshot.First().Metadata["scheduler.fire_time"]);
    }

    [Fact]
    public async Task TickAsync_PendingCursorProtectsReplay_WhenCursorAdvanceFails()
    {
        // Regression: if CreateRunAsync succeeds and AdvanceScheduleCursorAsync fails (simulating
        // a crash between the two), the durable pending cursor marker must prevent a restarted
        // scheduler from replaying the same occurrence after the claim TTL expires.
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var definitionStore = new FakeWorkflowDefinitionStore();
        var runStore = new FakeWorkflowRunStore();
        var progressStore = new FakeProgressStore();
        var clock = new TestClock(start);
        // Hourly cron so the 2-minute advance window contains exactly one occurrence (13:00).
        var definition = BuildCronDefinition(start, "0 * * * *");
        await definitionStore.TryCreateAsync(definition);

        var jobServiceA = new FakeWorkflowJobExecutor();
        var engineA = new WorkflowOrchestrationEngine(
            runStore, definitionStore, jobServiceA, progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var schedulerA = new WorkflowSchedulerBackgroundService(
            definitionStore, engineA, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        clock.Advance(TimeSpan.FromMinutes(65));

        // Inject cursor advance failure so the durable cursor stays behind, but the
        // pending cursor should still be set by the scheduler after run creation.
        definitionStore.NextAdvanceCursorFailure = new InvalidOperationException("Redis down");
        await schedulerA.TickAsync(CancellationToken.None);

        Assert.Single(runStore.Snapshot);

        // The pending cursor protects the occurrence even though the durable cursor
        // was never advanced. GetScheduleCursorAsync returns max(cursor, pending).
        var cursor = await definitionStore.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursor);
        Assert.True(cursor >= start.AddMinutes(60));

        // "Restart": new scheduler seeds from durable state, which now includes the
        // pending cursor. It must not replay the 13:00 occurrence.
        var jobServiceB = new FakeWorkflowJobExecutor();
        var engineB = new WorkflowOrchestrationEngine(
            runStore, definitionStore, jobServiceB, progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var schedulerB = new WorkflowSchedulerBackgroundService(
            definitionStore, engineB, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        await schedulerB.TickAsync(CancellationToken.None);
        Assert.Single(runStore.Snapshot);
    }

    [Fact]
    public async Task TickAsync_ClampsInMemoryCursorToUpdatedAt_WhenDefinitionUpdatedInPlace()
    {
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        var definition = BuildCronDefinition(start, "* * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Scheduler.TickAsync(CancellationToken.None);
        Assert.Single(harness.RunStore.Snapshot);

        var updatedAt = harness.Clock.GetUtcNow().AddMinutes(3);
        var updated = definition with { UpdatedAt = updatedAt };
        await harness.Definitions.SetAsync(updated);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Scheduler.TickAsync(CancellationToken.None);

        Assert.Equal(2, harness.RunStore.Snapshot.Count);
        var latestFireTime = harness.RunStore.Snapshot
            .Select(r => DateTimeOffset.Parse(
                r.Metadata["scheduler.fire_time"],
                System.Globalization.CultureInfo.InvariantCulture))
            .Max();
        Assert.True(latestFireTime > updatedAt,
            $"Fire time {latestFireTime:o} should be after UpdatedAt {updatedAt:o}");
    }

    [Fact]
    public async Task TickAsync_RetriesPendingCursorWrite_OnSubsequentTick()
    {
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        // Hourly cron so the advance window contains exactly one occurrence.
        var definition = BuildCronDefinition(start, "0 * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(65));

        harness.Definitions.NextAdvanceCursorFailure = new InvalidOperationException("Redis down");
        await harness.Scheduler.TickAsync(CancellationToken.None);

        Assert.Single(harness.RunStore.Snapshot);
        // The pending cursor provides a durable floor even though AdvanceScheduleCursorAsync
        // failed. GetScheduleCursorAsync returns max(cursor, pending).
        var cursorAfterFailure = await harness.Definitions.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursorAfterFailure);

        // The PendingCursorAt retry on the next tick should advance the actual cursor
        // and no duplicate run should be created within the same window.
        await harness.Scheduler.TickAsync(CancellationToken.None);

        var cursorAfterRetry = await harness.Definitions.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursorAfterRetry);
        Assert.Single(harness.RunStore.Snapshot);
    }

    [Fact]
    public async Task TickAsync_DoesNotAdvanceInMemoryCursor_WhenPendingCursorAndAdvanceBothFail()
    {
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        var definition = BuildCronDefinition(start, "0 * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(65));

        harness.Definitions.NextPendingCursorFailure = new InvalidOperationException("Redis blip");
        harness.Definitions.NextAdvanceCursorFailure = new InvalidOperationException("Redis blip");
        await harness.Scheduler.TickAsync(CancellationToken.None);

        Assert.Single(harness.RunStore.Snapshot);

        // The retry tick should durably advance the cursor and no duplicate
        // run should appear within the same cron window.
        await harness.Scheduler.TickAsync(CancellationToken.None);

        var cursor = await harness.Definitions.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursor);
        Assert.True(cursor >= start.AddMinutes(60));
        Assert.Single(harness.RunStore.Snapshot);
    }

    [Fact]
    public async Task TickAsync_WritesPendingCursor_ForPermanentValidationFailure()
    {
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var definitionStore = new FakeWorkflowDefinitionStore();
        var runStore = new FakeWorkflowRunStore();
        var progressStore = new FakeProgressStore();
        var clock = new TestClock(start);
        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf-invalid",
            Name = "invalid",
            Steps = Array.Empty<WorkflowStepDefinition>(),
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.Cron,
                CronExpression = "0 * * * *",
                Enabled = true
            },
            CreatedAt = start,
            UpdatedAt = start
        };
        await definitionStore.TryCreateAsync(definition);

        var engine = new WorkflowOrchestrationEngine(
            runStore, definitionStore, new FakeWorkflowJobExecutor(), progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var scheduler = new WorkflowSchedulerBackgroundService(
            definitionStore, engine, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        clock.Advance(TimeSpan.FromMinutes(65));
        await scheduler.TickAsync(CancellationToken.None);

        Assert.Empty(runStore.Snapshot);
        var cursor = await definitionStore.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursor);
        Assert.True(cursor >= start.AddMinutes(60));

        // A fresh scheduler must not replay the permanently-invalid occurrence.
        var engine2 = new WorkflowOrchestrationEngine(
            runStore, definitionStore, new FakeWorkflowJobExecutor(), progressStore, clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        var scheduler2 = new WorkflowSchedulerBackgroundService(
            definitionStore, engine2, clock, NullLogger<WorkflowSchedulerBackgroundService>.Instance);

        await scheduler2.TickAsync(CancellationToken.None);
        Assert.Empty(runStore.Snapshot);
    }

    [Fact]
    public async Task TickAsync_DoesNotCreateDuplicateRun_WhenProgressProjectionFailsInCreateRunAsync()
    {
        var start = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);
        var harness = BuildHarness(start);
        var definition = BuildCronDefinition(start, "0 * * * *");
        await harness.Definitions.TryCreateAsync(definition);

        harness.Clock.Advance(TimeSpan.FromMinutes(65));

        harness.Progress.NextSetProgressFailure = new InvalidOperationException("progress store down");
        await harness.Scheduler.TickAsync(CancellationToken.None);

        Assert.Single(harness.RunStore.Snapshot);

        await harness.Scheduler.TickAsync(CancellationToken.None);

        Assert.Single(harness.RunStore.Snapshot);
        var cursor = await harness.Definitions.GetScheduleCursorAsync(definition.WorkflowId);
        Assert.NotNull(cursor);
        Assert.True(cursor >= start.AddMinutes(60));
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
