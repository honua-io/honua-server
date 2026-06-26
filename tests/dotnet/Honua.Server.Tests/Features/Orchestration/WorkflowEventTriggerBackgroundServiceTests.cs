// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Orchestration;

public sealed class WorkflowEventTriggerBackgroundServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 4, 16, 12, 0, 0, TimeSpan.Zero);

    // ---------------- Change-feed trigger ----------------

    [Fact]
    public async Task TickAsync_ChangeFeed_FiresRun_WhenGenerationAdvances()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildChangeFeedDefinition("wf-cdc", watchedLayers: [7]);
        await harness.Definitions.TryCreateAsync(definition);

        harness.ChangeFeed.LatestGeneration = 42;

        await harness.Service.TickAsync(CancellationToken.None);

        var runs = await harness.RunStore.ListActiveAsync();
        Assert.Single(runs);
        Assert.Equal(WorkflowTriggerKind.ChangeFeed, runs[0].TriggerKind);
        Assert.Equal("42", runs[0].Metadata["trigger.change_feed.generation"]);
    }

    [Fact]
    public async Task TickAsync_ChangeFeed_DoesNotFire_WhenNoGenerationAdvance()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildChangeFeedDefinition("wf-cdc", watchedLayers: [7]);
        await harness.Definitions.TryCreateAsync(definition);

        // Probe reports no advance (null).
        harness.ChangeFeed.LatestGeneration = null;

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Empty(await harness.RunStore.ListActiveAsync());
    }

    [Fact]
    public async Task TickAsync_ChangeFeed_DoesNotRefire_WhenGenerationUnchanged()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildChangeFeedDefinition("wf-cdc", watchedLayers: [7]);
        await harness.Definitions.TryCreateAsync(definition);

        harness.ChangeFeed.LatestGeneration = 42;
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Single(harness.RunStore.Snapshot);

        // Same generation on the next tick must not fire again. The probe is invoked with the
        // advanced cursor (since=42), so it reports no further advance.
        harness.ChangeFeed.LatestGeneration = 42;
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Single(harness.RunStore.Snapshot);
    }

    [Fact]
    public async Task TickAsync_ChangeFeed_FiresAgain_OnFurtherAdvance()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildChangeFeedDefinition("wf-cdc", watchedLayers: [7]);
        await harness.Definitions.TryCreateAsync(definition);

        harness.ChangeFeed.LatestGeneration = 42;
        await harness.Service.TickAsync(CancellationToken.None);

        harness.ChangeFeed.LatestGeneration = 50;
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(2, harness.RunStore.Snapshot.Count);
        var generations = harness.RunStore.Snapshot
            .Select(r => r.Metadata["trigger.change_feed.generation"])
            .ToArray();
        Assert.Contains("42", generations);
        Assert.Contains("50", generations);
    }

    [Fact]
    public async Task TickAsync_ChangeFeed_HaSingleFire_AcrossTwoReplicas()
    {
        // Two replicas share one durable definition store (the HA fire-claim lives there).
        var definitionStore = new FakeWorkflowDefinitionStore();
        var runStore = new FakeWorkflowRunStore();
        var definition = BuildChangeFeedDefinition("wf-cdc", watchedLayers: [7]);
        await definitionStore.TryCreateAsync(definition);

        var replicaA = BuildReplica(definitionStore, runStore, latestGeneration: 99);
        var replicaB = BuildReplica(definitionStore, runStore, latestGeneration: 99);

        // Both replicas tick for the same generation; only one may create a run.
        await Task.WhenAll(
            replicaA.TickAsync(CancellationToken.None),
            replicaB.TickAsync(CancellationToken.None));

        Assert.Single(runStore.Snapshot);
    }

    [Fact]
    public async Task TickAsync_ChangeFeed_SkipsDisabledTrigger()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildChangeFeedDefinition("wf-cdc", watchedLayers: [7]) with
        {
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.ChangeFeed,
                WatchedLayerIds = [7],
                Enabled = false
            }
        };
        Assert.False(definition.Trigger!.Enabled);
        await harness.Definitions.TryCreateAsync(definition);

        harness.ChangeFeed.LatestGeneration = 42;
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Empty(await harness.RunStore.ListActiveAsync());
    }

    [Fact]
    public async Task TickAsync_ChangeFeed_RetriesSameGeneration_OnTransientCreateFailure()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildChangeFeedDefinition("wf-cdc", watchedLayers: [7]);
        await harness.Definitions.TryCreateAsync(definition);

        harness.ChangeFeed.LatestGeneration = 42;
        harness.RunStore.NextTryCreateFailure = new InvalidOperationException("transient blip");

        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Empty(harness.RunStore.Snapshot);

        // The cursor must not have advanced; the claim is released so the next tick retries.
        var cursor = await harness.Definitions.GetTriggerCursorAsync(
            definition.WorkflowId, RedisWorkflowDefinitionStore.ChangeFeedCursorKind);
        Assert.Null(cursor);

        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Single(harness.RunStore.Snapshot);
    }

    // ---------------- Object-store trigger ----------------

    [Fact]
    public async Task TickAsync_ObjectStore_FiresRun_WhenNewObjectLandsAfterSeed()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildObjectStoreDefinition("wf-obj", storeId: "drop", prefix: "in/");
        await harness.Definitions.TryCreateAsync(definition);

        // First tick seeds the cursor with the existing newest object — no fire (avoids backlog stampede).
        harness.ObjectStore.Newest = new ObjectStoreProbeResult
        {
            Key = "in/a.csv",
            LastModified = Start
        };
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Empty(harness.RunStore.Snapshot);

        // A new object lands after the seed → fire.
        harness.ObjectStore.Newest = new ObjectStoreProbeResult
        {
            Key = "in/b.csv",
            LastModified = Start.AddMinutes(1)
        };
        await harness.Service.TickAsync(CancellationToken.None);

        var run = Assert.Single(harness.RunStore.Snapshot);
        Assert.Equal(WorkflowTriggerKind.ObjectStore, run.TriggerKind);
        Assert.Equal("in/b.csv", run.Metadata["trigger.object_store.key"]);
    }

    [Fact]
    public async Task TickAsync_ObjectStore_DoesNotFire_WhenNoNewObject()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildObjectStoreDefinition("wf-obj", storeId: "drop", prefix: "in/");
        await harness.Definitions.TryCreateAsync(definition);

        var existing = new ObjectStoreProbeResult { Key = "in/a.csv", LastModified = Start };
        harness.ObjectStore.Newest = existing;
        await harness.Service.TickAsync(CancellationToken.None); // seed

        // Same newest object on the next tick → no fire.
        harness.ObjectStore.Newest = existing;
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Empty(harness.RunStore.Snapshot);
    }

    [Fact]
    public async Task TickAsync_ObjectStore_DoesNotFire_WhenStoreEmpty()
    {
        var harness = new EventTriggerHarness(Start);
        var definition = BuildObjectStoreDefinition("wf-obj", storeId: "drop", prefix: "in/");
        await harness.Definitions.TryCreateAsync(definition);

        harness.ObjectStore.Newest = null; // empty store/prefix
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Empty(harness.RunStore.Snapshot);
    }

    // ---------------- Helpers ----------------

    private static WorkflowEventTriggerBackgroundService BuildReplica(
        FakeWorkflowDefinitionStore definitions,
        FakeWorkflowRunStore runStore,
        long? latestGeneration)
    {
        var clock = new TestClock(Start);
        var engine = new WorkflowOrchestrationEngine(
            runStore, definitions, new FakeWorkflowJobExecutor(), new FakeProgressStore(), clock,
            NullLogger<WorkflowOrchestrationEngine>.Instance);
        return new WorkflowEventTriggerBackgroundService(
            definitions,
            engine,
            new FakeChangeFeedProbe { LatestGeneration = latestGeneration },
            new FakeObjectStoreProbe(),
            clock,
            NullLogger<WorkflowEventTriggerBackgroundService>.Instance);
    }

    private static WorkflowDefinition BuildChangeFeedDefinition(string id, IReadOnlyList<int> watchedLayers)
        => BuildBaseDefinition(id) with
        {
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.ChangeFeed,
                WatchedLayerIds = watchedLayers,
                Enabled = true
            }
        };

    private static WorkflowDefinition BuildObjectStoreDefinition(string id, string storeId, string prefix)
        => BuildBaseDefinition(id) with
        {
            Trigger = new WorkflowTrigger
            {
                Kind = WorkflowTriggerKind.ObjectStore,
                ObjectStore = new ObjectStoreTriggerConfig
                {
                    StoreId = storeId,
                    Prefix = prefix
                },
                Enabled = true
            }
        };

    private static WorkflowDefinition BuildBaseDefinition(string id) => new()
    {
        WorkflowId = id,
        Name = "event-triggered",
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
        CreatedAt = Start,
        UpdatedAt = Start
    };

    private sealed class EventTriggerHarness
    {
        public EventTriggerHarness(DateTimeOffset start)
        {
            RunStore = new FakeWorkflowRunStore();
            Definitions = new FakeWorkflowDefinitionStore();
            ChangeFeed = new FakeChangeFeedProbe();
            ObjectStore = new FakeObjectStoreProbe();
            Clock = new TestClock(start);
            Engine = new WorkflowOrchestrationEngine(
                RunStore, Definitions, new FakeWorkflowJobExecutor(), new FakeProgressStore(), Clock,
                NullLogger<WorkflowOrchestrationEngine>.Instance);
            Service = new WorkflowEventTriggerBackgroundService(
                Definitions, Engine, ChangeFeed, ObjectStore, Clock,
                NullLogger<WorkflowEventTriggerBackgroundService>.Instance);
        }

        public FakeWorkflowRunStore RunStore { get; }
        public FakeWorkflowDefinitionStore Definitions { get; }
        public FakeChangeFeedProbe ChangeFeed { get; }
        public FakeObjectStoreProbe ObjectStore { get; }
        public TestClock Clock { get; }
        public WorkflowOrchestrationEngine Engine { get; }
        public WorkflowEventTriggerBackgroundService Service { get; }
    }

    private sealed class FakeChangeFeedProbe : IChangeFeedGenerationProbe
    {
        public long? LatestGeneration { get; set; }

        public Task<long?> GetLatestGenerationAsync(
            long sinceGeneration,
            IReadOnlyList<int> watchedLayerIds,
            CancellationToken cancellationToken = default)
        {
            if (LatestGeneration is { } latest && latest > sinceGeneration)
            {
                return Task.FromResult<long?>(latest);
            }

            return Task.FromResult<long?>(null);
        }
    }

    private sealed class FakeObjectStoreProbe : IObjectStoreTriggerProbe
    {
        public ObjectStoreProbeResult? Newest { get; set; }

        public Task<ObjectStoreProbeResult?> ProbeNewestAsync(
            ObjectStoreTriggerConfig config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Newest);
    }
}
