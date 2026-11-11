// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Tiles;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Two-node proof that tile-export and geoprocessing submissions share one admission lease
/// and one durable active set (#4691). Each node is an independent store,
/// evaluator, and coordinator; the tile-export and geoprocessing services on a node share
/// that node's coordinator. The services under test are the shipped ones.
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class TileExportAdmissionSharedLeaseTests
    : IClassFixture<DatabaseFixtureAdapter>, IAsyncLifetime
{
    private const int RetryAfterSeconds = 7;

    private static readonly IOptionsMonitor<GeoprocessingExecutorOptions> ExecutorOptions =
        new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions());

    private readonly DatabaseFixtureAdapter _database;
    private readonly ITestOutputHelper _output;
    private PostgresJobSubstrateHarness _jobs = null!;
    private PostgresExecutionJobStore _observerStore = null!;

    public TileExportAdmissionSharedLeaseTests(DatabaseFixtureAdapter database, ITestOutputHelper output)
    {
        _database = database;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _jobs = await PostgresJobSubstrateHarness.CreateAsync(_database, nameof(TileExportAdmissionSharedLeaseTests));
        _observerStore = _jobs.JobStore;
    }

    public async Task DisposeAsync() => await _jobs.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SharedLease_UnderTheSameInterleaving_AdmitsExactlyTheLimit()
    {
        var limits = Limits(global: 1, perPartition: 100, cost: 1000);
        var rendezvous = new Rendezvous(parties: 2, TimeSpan.FromSeconds(1));
        var nodeA = CreateNode("node-a", limits, wrapStore: rendezvous.Wrap);
        var nodeB = CreateNode("node-b", limits, wrapStore: rendezvous.Wrap);

        var (attempts, sampler) = await BurstAsync(
        [
            () => SubmitTileAsync(nodeA, "race-tile", MapPlan()),
            () => SubmitGeoprocessingAsync(nodeB, "tenant-b", "bob", "race-gp", BufferPlan()),
        ]);
        WriteLedger(nameof(SharedLease_UnderTheSameInterleaving_AdmitsExactlyTheLimit), limits, attempts, sampler);

        rendezvous.AllArrived.Should().BeFalse("the shared lease does not let the second node create until the first has released");
        attempts.Select(attempt => attempt.Kind).Should().BeEquivalentTo(["tile", "gp"]);
        attempts.Count(attempt => attempt.Admitted).Should().Be(1);
        var denied = attempts.Single(attempt => !attempt.Admitted);
        denied.Outcome.Should().Be(ExecutionAdmissionOutcome.Denied);
        denied.Dimension.Should().Be(ExecutionAdmissionDimension.Backpressure);
        denied.PolicyRef.Should().StartWith("backpressure:");
        denied.RetryAfterSeconds.Should().Be(RetryAfterSeconds);
        sampler.MaxGlobal.Should().Be(1);
        (await _observerStore.ListActiveAsync()).Should().ContainSingle();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task NodeLocalGatesOnly_UnderTheSameInterleaving_Oversubscribe()
    {
        // Negative control: node-local gates only. Holding each node's create until both have
        // evaluated admits a tile export and a geoprocessing job against a global limit of one.
        var limits = Limits(global: 1, perPartition: 100, cost: 1000);
        var rendezvous = new Rendezvous(parties: 2, TimeSpan.FromSeconds(3));
        var nodeA = CreateNode("node-a", limits, sharedLease: false, wrapStore: rendezvous.Wrap);
        var nodeB = CreateNode("node-b", limits, sharedLease: false, wrapStore: rendezvous.Wrap);

        var (attempts, sampler) = await BurstAsync(
        [
            () => SubmitTileAsync(nodeA, "race-tile", MapPlan()),
            () => SubmitGeoprocessingAsync(nodeB, "tenant-b", "bob", "race-gp", BufferPlan()),
        ]);
        WriteLedger(nameof(NodeLocalGatesOnly_UnderTheSameInterleaving_Oversubscribe), limits, attempts, sampler);

        rendezvous.AllArrived.Should().BeTrue("both nodes evaluated admission before either created its record");
        attempts.Should().OnlyContain(attempt => attempt.Admitted);
        sampler.MaxGlobal.Should().Be(2);
        (await _observerStore.ListActiveAsync()).Should().HaveCount(2, "per-node gates oversubscribe a global limit of 1");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task TwoNodes_MixedBurst_NeverExceedsConfiguredLimits()
    {
        var limits = Limits(global: 4, perPartition: 2, cost: 3);
        var nodeA = CreateNode("node-a", limits);
        var nodeB = CreateNode("node-b", limits);

        // Offered load exceeds every configured denominator: global (all of these), each
        // kind's partition (tiles share one resource, buffers share tenant-a, warps share
        // tenant-b), and partition cost (each submission costs at least one, and a warp
        // costs its raster class). The numbers below are the limits, not a prior-run snapshot.
        const int tiles = 6;
        const int buffers = 6;
        const int warps = 4;
        (tiles + buffers + warps).Should().BeGreaterThan(limits.MaxConcurrentJobsGlobal);
        tiles.Should().BeGreaterThan(limits.MaxConcurrentJobsPerPartition);
        buffers.Should().BeGreaterThan(limits.MaxConcurrentJobsPerPartition);
        warps.Should().BeGreaterThan(limits.MaxConcurrentJobsPerPartition);
        ((double)tiles).Should().BeGreaterThan(limits.MaxCostWeightPerPartition);
        ((double)buffers).Should().BeGreaterThan(limits.MaxCostWeightPerPartition);
        ((double)warps).Should().BeGreaterThan(limits.MaxCostWeightPerPartition);

        var submissions = new List<Func<Task<Attempt>>>();
        for (var i = 0; i < tiles; i++)
        {
            var node = i % 2 == 0 ? nodeA : nodeB;
            var key = $"tile-{i}";
            submissions.Add(() => SubmitTileAsync(node, key, MapPlan()));
        }

        for (var i = 0; i < buffers; i++)
        {
            var node = i % 2 == 0 ? nodeB : nodeA;
            var key = $"buffer-{i}";
            submissions.Add(() => SubmitGeoprocessingAsync(node, "tenant-a", "alice", key, BufferPlan()));
        }

        for (var i = 0; i < warps; i++)
        {
            var node = i % 2 == 0 ? nodeA : nodeB;
            var key = $"warp-{i}";
            submissions.Add(() => SubmitGeoprocessingAsync(node, "tenant-b", "carol", key, WarpPlan()));
        }

        var (attempts, sampler) = await BurstAsync(submissions);
        WriteLedger(nameof(TwoNodes_MixedBurst_NeverExceedsConfiguredLimits), limits, attempts, sampler);

        attempts.Should().Contain(attempt => attempt.Admitted);
        attempts.Should().Contain(attempt => !attempt.Admitted);
        attempts.Where(attempt => !attempt.Admitted).Should().OnlyContain(attempt =>
            attempt.RetryAfterSeconds == limits.DefaultRetryAfterSeconds
            && (attempt.Dimension == ExecutionAdmissionDimension.Backpressure
                || attempt.Dimension == ExecutionAdmissionDimension.Concurrency
                || attempt.Dimension == ExecutionAdmissionDimension.Cost));

        sampler.MaxGlobal.Should().BeLessThanOrEqualTo(limits.MaxConcurrentJobsGlobal);
        sampler.MaxPerPartition.Values.Should().OnlyContain(count => count <= limits.MaxConcurrentJobsPerPartition);
        sampler.MaxCostPerPartition.Values.Should().OnlyContain(cost => cost <= limits.MaxCostWeightPerPartition);

        var active = await _observerStore.ListActiveAsync();
        active.Count.Should().BeLessThanOrEqualTo(limits.MaxConcurrentJobsGlobal);
        foreach (var partition in active.GroupBy(ScopeKey))
        {
            partition.Count().Should().BeLessThanOrEqualTo(limits.MaxConcurrentJobsPerPartition);
            partition.Sum(ReadCost).Should().BeLessThanOrEqualTo(limits.MaxCostWeightPerPartition);
        }
    }

    private static ExecutionAdmissionOptions Limits(int global, int perPartition, double cost) => new()
    {
        Enabled = true,
        MaxConcurrentJobsGlobal = global,
        MaxConcurrentJobsPerPartition = perPartition,
        MaxCostWeightPerPartition = cost,
        MaxSubmissionsPerWindow = 1000,
        RateWindowSeconds = 60,
        DefaultRetryAfterSeconds = RetryAfterSeconds
    };

    private Node CreateNode(
        string name,
        ExecutionAdmissionOptions limits,
        bool sharedLease = true,
        Func<IExecutionJobStore, IExecutionJobStore>? wrapStore = null)
    {
        var nodeStore = _jobs.CreateNodeJobStore();
        var store = wrapStore is null ? nodeStore : wrapStore(nodeStore);

        var admission = AdmissionNodeComposition.Compose(
            nodeStore,
            store,
            new StaticOptionsMonitor<ExecutionAdmissionOptions>(limits),
            sharedLease);

        var queue = Substitute.For<IJobQueue>();
        var progress = Substitute.For<IUniversalProgressStore>();
        var geoprocessing = new GeoprocessingJobService(
            progress,
            [Substitute.For<IJobCancellationNotifier>()],
            admission.OperatorAuthorization,
            admission.OperatorApproval,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            ExecutorOptions,
            store,
            queue,
            admissionEvaluator: admission.Evaluator,
            admissionCoordinator: admission.Coordinator);
        var tiles = new TileExportJobService(
            TimeProvider.System,
            Options.Create(new CloudStorageOptions()),
            NullLogger<TileExportJobService>.Instance,
            store,
            queue,
            storage: null,
            admissionEvaluator: admission.Evaluator,
            admissionCoordinator: admission.Coordinator);

        return new Node(name, geoprocessing, tiles);
    }

    private static async Task<Attempt> SubmitTileAsync(Node node, string key, TileExportJobPlan plan)
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            var job = await node.Tiles.SubmitAsync(plan, key, correlationId: null, Principal("tenant-tiles", "tiles"), CancellationToken.None);
            return new Attempt(node.Name, "tile", "tenant-tiles", "tiles", key, at, true, job.OperationId,
                ExecutionAdmissionOutcome.Admitted, null, null, null);
        }
        catch (TileExportAdmissionException ex)
        {
            return new Attempt(node.Name, "tile", "tenant-tiles", "tiles", key, at, false, null,
                ex.Outcome, ex.DenyingDimension, ex.PolicyRef, ex.RetryAfterSeconds);
        }
    }

    private static async Task<Attempt> SubmitGeoprocessingAsync(
        Node node,
        string tenant,
        string user,
        string key,
        AnalysisPlan plan)
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            var job = await node.Geoprocessing.SubmitJobAsync(plan, key, Principal(tenant, user));
            return new Attempt(node.Name, "gp", tenant, user, key, at, true, job.OperationId,
                ExecutionAdmissionOutcome.Admitted, null, null, null);
        }
        catch (GeoprocessingAdmissionException ex)
        {
            return new Attempt(node.Name, "gp", tenant, user, key, at, false, null,
                ex.Outcome, ex.DenyingDimension, ex.PolicyRef, ex.RetryAfterSeconds);
        }
    }

    private async Task<(IReadOnlyList<Attempt> Attempts, ActiveSampler Sampler)> BurstAsync(
        IReadOnlyList<Func<Task<Attempt>>> submissions)
    {
        var sampler = new ActiveSampler();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var sampling = sampler.RunAsync(_observerStore, stop.Token);
        var running = submissions
            .Select(submit => Task.Run(async () =>
            {
                await release.Task;
                return await submit();
            }))
            .ToArray();

        release.SetResult();
        var attempts = await Task.WhenAll(running);
        await stop.CancelAsync();
        await sampling;
        await sampler.SampleAsync(_observerStore);
        return (attempts, sampler);
    }

    private void WriteLedger(
        string scenario,
        ExecutionAdmissionOptions limits,
        IEnumerable<Attempt> attempts,
        ActiveSampler sampler)
    {
        _output.WriteLine(
            $"scenario={scenario} limits: global={limits.MaxConcurrentJobsGlobal} partition={limits.MaxConcurrentJobsPerPartition} " +
            $"cost={limits.MaxCostWeightPerPartition.ToString(CultureInfo.InvariantCulture)}");
        foreach (var attempt in attempts.OrderBy(attempt => attempt.At))
        {
            _output.WriteLine(string.Join('\t',
                attempt.Node, attempt.Kind, attempt.Tenant, attempt.User, attempt.Key,
                attempt.Admitted ? "admitted" : "denied",
                attempt.Dimension?.ToString() ?? "-", attempt.PolicyRef ?? "-",
                attempt.RetryAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-",
                attempt.JobId ?? "-"));
        }

        _output.WriteLine(
            $"observed: samples={sampler.Samples} maxGlobal={sampler.MaxGlobal} " +
            $"maxPartition={string.Join(',', sampler.MaxPerPartition.Select(pair => $"{pair.Key}:{pair.Value}"))} " +
            $"maxCost={string.Join(',', sampler.MaxCostPerPartition.Select(pair => $"{pair.Key}:{pair.Value.ToString(CultureInfo.InvariantCulture)}"))}");
    }

    private static ClaimsPrincipal Principal(string tenant, string user)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user),
                new Claim(ClaimTypes.NameIdentifier, user),
                new Claim("tenant_id", tenant)
            ],
            "Test"));

    /// <summary>
    /// Smallest valid map export plan from TileExportJobServiceTests.CreatePlan.
    /// </summary>
    private static TileExportJobPlan MapPlan()
        => new()
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = "world-basemap",
            Source = new TileExportMapSourceDescriptor(
                42,
                [new("0", "default", 1)],
                "provider-revision-9",
                null),
            ZoomLevels = [0, 2],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 10_000,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };

    private static AnalysisPlan BufferPlan() => new()
    {
        PlanId = "plan-buffer",
        IntentId = "intent-buffer",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-1",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "geometry.buffer",
                Inputs = new Dictionary<string, string>
                {
                    ["wkb"] = "AAAA",
                    ["srid"] = "4326",
                    ["distance"] = "100"
                }
            }
        ]
    };

    private static AnalysisPlan WarpPlan() => new()
    {
        PlanId = "plan-warp",
        IntentId = "intent-warp",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-1",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "gdal.gdalwarp",
                Inputs = new Dictionary<string, string>
                {
                    ["source"] = "AAAA",
                    ["targetSrs"] = "3857"
                }
            }
        ]
    };

    private static string ScopeKey(ExecutionJobRecord job)
        => $"{job.Spec.Kind}:{ReadPartition(job) ?? "(default)"}";

    private static string? ReadPartition(ExecutionJobRecord job)
    {
        if (job.Spec.Parameters.TryGetValue(ExecutionAdmissionEvaluator.PartitionKeyParameterKey, out var value)
            && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        return string.IsNullOrEmpty(job.Concurrency.PartitionKey) ? null : job.Concurrency.PartitionKey;
    }

    private static double ReadCost(ExecutionJobRecord job)
    {
        if (job.Spec.Parameters.TryGetValue(ExecutionAdmissionEvaluator.CostWeightParameterKey, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        if (job.AdmissionCostWeight is { } pinned && pinned > 0)
        {
            return pinned;
        }

        return 1.0;
    }

    private sealed record Attempt(
        string Node,
        string Kind,
        string Tenant,
        string User,
        string Key,
        DateTimeOffset At,
        bool Admitted,
        string? JobId,
        ExecutionAdmissionOutcome? Outcome,
        ExecutionAdmissionDimension? Dimension,
        string? PolicyRef,
        int? RetryAfterSeconds);

    private sealed record Node(string Name, GeoprocessingJobService Geoprocessing, TileExportJobService Tiles);

    private sealed class ActiveSampler
    {
        private readonly object _sync = new();

        public int Samples { get; private set; }

        public int MaxGlobal { get; private set; }

        public Dictionary<string, int> MaxPerPartition { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, double> MaxCostPerPartition { get; } = new(StringComparer.Ordinal);

        public async Task RunAsync(IExecutionJobStore store, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SampleAsync(store);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(2), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        public async Task SampleAsync(IExecutionJobStore store)
        {
            var active = await store.ListActiveAsync();
            lock (_sync)
            {
                Samples++;
                MaxGlobal = Math.Max(MaxGlobal, active.Count);
                foreach (var partition in active.GroupBy(ScopeKey))
                {
                    MaxPerPartition[partition.Key] = Math.Max(
                        MaxPerPartition.GetValueOrDefault(partition.Key), partition.Count());
                    MaxCostPerPartition[partition.Key] = Math.Max(
                        MaxCostPerPartition.GetValueOrDefault(partition.Key), partition.Sum(ReadCost));
                }
            }
        }
    }

    /// <summary>
    /// Holds every wrapped node's record creation until <c>parties</c> nodes have reached it (or a
    /// timeout passes), forcing the evaluations to complete before any record becomes visible.
    /// </summary>
    private sealed class Rendezvous(int parties, TimeSpan timeout)
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool AllArrived => _allArrived.Task.IsCompleted;

        public InterceptingJobStore Wrap(IExecutionJobStore inner) => new(inner)
        {
            BeforeCreate = ArriveAsync
        };

        private async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= parties)
            {
                _allArrived.TrySetResult();
            }

            await Task.WhenAny(_allArrived.Task, Task.Delay(timeout));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
