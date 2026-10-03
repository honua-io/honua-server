// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Multi-node execution-admission proofs (#3853). Every test composes two or more independent
/// "server nodes" — each with its own Redis connection, durable job store, admission evaluator,
/// admission coordinator, and job service, sharing nothing in-process — against one real Redis,
/// the substrate that holds the durable job records and their active-set index in production.
/// Submissions are released together from a barrier across nodes, and an independent observer
/// samples the durable active set throughout, so the assertions are on what any node could see
/// at any observation point rather than on a single evaluator's arithmetic.
/// </summary>
/// <remarks>
/// Each test writes its request ledger (node, tenant, principal, timestamp, idempotency key,
/// limits, outcome, dimension, Retry-After, job id) and the maximum observed global, partition,
/// and cost values to the test output. The expected values are derived from the configured
/// limits, never from a snapshot of current output.
/// </remarks>
[Protocol(TestProtocols.GPServer)]
public sealed class ExecutionAdmissionMultiNodeTests
    : IClassFixture<ExecutionAdmissionMultiNodeTests.RedisServer>, IAsyncLifetime
{
    private const int RetryAfterSeconds = 7;
    private const string DefaultPartition = "(default)";

    private static readonly IOptionsMonitor<GeoprocessingExecutorOptions> ExecutorOptions =
        new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions());

    private readonly RedisServer _redis;
    private readonly ITestOutputHelper _output;
    private readonly List<Node> _nodes = [];
    private ConnectionMultiplexer _observer = null!;
    private RedisExecutionJobStore _observerStore = null!;

    public ExecutionAdmissionMultiNodeTests(RedisServer redis, ITestOutputHelper output)
    {
        _redis = redis;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var options = ConfigurationOptions.Parse(_redis.ConnectionString);
        options.AllowAdmin = true;
        _observer = await ConnectionMultiplexer.ConnectAsync(options);
        foreach (var endpoint in _observer.GetEndPoints())
        {
            await _observer.GetServer(endpoint).FlushDatabaseAsync();
        }

        _observerStore = new RedisExecutionJobStore(_observer, NullLogger<RedisExecutionJobStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        await _observer.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Concurrency denominators under a cross-node barrier
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task TwoNodes_BarrierBurstBeyondGlobalAndPartitionLimits_NeverOversubscribe()
    {
        var limits = Limits(global: 5, perPartition: 3);
        var nodeA = await CreateNodeAsync("node-a", limits);
        var nodeB = await CreateNodeAsync("node-b", limits);

        // 24 submissions from three tenants, alternating nodes: every limit is exceeded several
        // times over. Serialized admission grants exactly min(global, sum(min(partition, 8))) = 5.
        var submissions = new List<Func<Task<Attempt>>>();
        foreach (var tenant in new[] { "tenant-a", "tenant-b", "tenant-c" })
        {
            for (var i = 0; i < 8; i++)
            {
                var node = i % 2 == 0 ? nodeA : nodeB;
                var key = $"{tenant}-burst-{i}";
                var user = $"{tenant}-user";
                submissions.Add(() => SubmitAsync(node, tenant, user, key, BufferPlan(), cost: 1));
            }
        }

        var (attempts, sampler) = await BurstAsync(submissions);
        WriteLedger(nameof(TwoNodes_BarrierBurstBeyondGlobalAndPartitionLimits_NeverOversubscribe), limits, attempts, sampler);

        var admitted = attempts.Where(a => a.Admitted).ToList();
        admitted.Should().HaveCount(5, "the global limit is the binding denominator for 24 requests");
        admitted.GroupBy(a => a.Tenant).Should().OnlyContain(g => g.Count() <= 3);
        sampler.MaxGlobal.Should().BeLessThanOrEqualTo(5);
        sampler.MaxPerPartition.Values.Should().OnlyContain(count => count <= 3);

        var rejected = attempts.Where(a => !a.Admitted).ToList();
        rejected.Should().HaveCount(19);
        rejected.Should().OnlyContain(a =>
            a.Outcome == ExecutionAdmissionOutcome.Denied
            && (a.Dimension == ExecutionAdmissionDimension.Backpressure || a.Dimension == ExecutionAdmissionDimension.Concurrency)
            && a.RetryAfterSeconds == RetryAfterSeconds);

        await AssertRejectionsLeftNoTraceAsync(rejected, admitted);
        (await _observerStore.ListActiveAsync()).Should().HaveCount(5);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task TwoNodes_TenantSaturatesItsPartition_OtherTenantIsStillAdmitted()
    {
        var limits = Limits(global: 100, perPartition: 3);
        var nodeA = await CreateNodeAsync("node-a", limits);
        var nodeB = await CreateNodeAsync("node-b", limits);

        // Tenant A floods both nodes with a backlog; tenant B submits during the same burst.
        var submissions = new List<Func<Task<Attempt>>>();
        for (var i = 0; i < 12; i++)
        {
            var node = i % 2 == 0 ? nodeA : nodeB;
            var key = $"flood-{i}";
            submissions.Add(() => SubmitAsync(node, "tenant-a", "tenant-a-user", key, BufferPlan(), cost: 1));
        }

        for (var i = 0; i < 3; i++)
        {
            var node = i % 2 == 0 ? nodeB : nodeA;
            var key = $"victim-{i}";
            submissions.Add(() => SubmitAsync(node, "tenant-b", "tenant-b-user", key, BufferPlan(), cost: 1));
        }

        var (attempts, sampler) = await BurstAsync(submissions);
        WriteLedger(nameof(TwoNodes_TenantSaturatesItsPartition_OtherTenantIsStillAdmitted), limits, attempts, sampler);

        attempts.Count(a => a.Tenant == "tenant-a" && a.Admitted).Should().Be(3);
        attempts.Where(a => a.Tenant == "tenant-a" && !a.Admitted).Should().HaveCount(9)
            .And.OnlyContain(a => a.Dimension == ExecutionAdmissionDimension.Concurrency && a.RetryAfterSeconds == RetryAfterSeconds);

        // Tenant A's saturation does not consume tenant B's partition: all of B's work is admitted.
        attempts.Where(a => a.Tenant == "tenant-b").Should().HaveCount(3).And.OnlyContain(a => a.Admitted);
        sampler.MaxPerPartition["tenant-a"].Should().BeLessThanOrEqualTo(3);
        sampler.MaxPerPartition["tenant-b"].Should().BeLessThanOrEqualTo(3);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task TwoNodes_WeightedCostBurst_NeverExceedsPartitionCostLimit()
    {
        var limits = Limits(global: 100, perPartition: 100, cost: 10);
        var nodeA = await CreateNodeAsync("node-a", limits);
        var nodeB = await CreateNodeAsync("node-b", limits);

        // gdal.gdalwarp is charged its raster resource class (4); geometry.buffer is charged 1.
        // 25 units requested against a 10-unit partition budget.
        var submissions = new List<Func<Task<Attempt>>>();
        for (var i = 0; i < 5; i++)
        {
            var heavyNode = i % 2 == 0 ? nodeA : nodeB;
            var lightNode = i % 2 == 0 ? nodeB : nodeA;
            var heavyKey = $"heavy-{i}";
            var lightKey = $"light-{i}";
            submissions.Add(() => SubmitAsync(heavyNode, "tenant-a", "tenant-a-user", heavyKey, WarpPlan(), cost: 4));
            submissions.Add(() => SubmitAsync(lightNode, "tenant-a", "tenant-a-user", lightKey, BufferPlan(), cost: 1));
        }

        var (attempts, sampler) = await BurstAsync(submissions);
        WriteLedger(nameof(TwoNodes_WeightedCostBurst_NeverExceedsPartitionCostLimit), limits, attempts, sampler);

        var admittedCost = attempts.Where(a => a.Admitted).Sum(a => a.Cost);
        admittedCost.Should().BeLessThanOrEqualTo(10);
        // Serialized greedy admission only rejects a request whose cost no longer fits, so the
        // admitted total is within (limit - largest request) of the limit: at least 7 of 10.
        admittedCost.Should().BeGreaterThanOrEqualTo(7);
        sampler.MaxCostPerPartition["tenant-a"].Should().BeLessThanOrEqualTo(10);

        var rejected = attempts.Where(a => !a.Admitted).ToList();
        rejected.Should().NotBeEmpty().And.OnlyContain(a =>
            a.Dimension == ExecutionAdmissionDimension.Cost && a.RetryAfterSeconds == RetryAfterSeconds);

        // The durable cost stamps are the ones the evaluator reads back; they add up to the same total.
        var activeCost = (await _observerStore.ListActiveAsync()).Sum(ReadCost);
        activeCost.Should().Be(admittedCost);
        await AssertRejectionsLeftNoTraceAsync(rejected, attempts.Where(a => a.Admitted).ToList());
    }

    // -----------------------------------------------------------------------
    // Shared rate limit: not multiplied by node count, not reset by restart
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task TwoNodes_SharedRateLimit_IsNotMultipliedByNodesNorResetByRestart()
    {
        var limits = Limits(global: 100, perPartition: 100, rate: 4);
        var nodeA = await CreateNodeAsync("node-a", limits);
        var nodeB = await CreateNodeAsync("node-b", limits);

        var submissions = new List<Func<Task<Attempt>>>();
        for (var i = 0; i < 10; i++)
        {
            var node = i % 2 == 0 ? nodeA : nodeB;
            var key = $"rate-{i}";
            submissions.Add(() => SubmitAsync(node, "tenant-r", "rate-user", key, BufferPlan(), cost: 1));
        }

        var (attempts, sampler) = await BurstAsync(submissions);
        WriteLedger(nameof(TwoNodes_SharedRateLimit_IsNotMultipliedByNodesNorResetByRestart), limits, attempts, sampler);

        // Two nodes with per-node buckets would have granted 8.
        attempts.Count(a => a.Admitted).Should().Be(4);
        attempts.Where(a => !a.Admitted).Should().OnlyContain(a =>
            a.Outcome == ExecutionAdmissionOutcome.Throttled
            && a.Dimension == ExecutionAdmissionDimension.Rate
            && a.RetryAfterSeconds == RetryAfterSeconds);

        // Restart node A: a fresh process has empty local state but must inherit the shared window.
        await RestartAsync(nodeA);
        var restartedA = await CreateNodeAsync("node-a-restarted", limits);

        var afterRestart = new[]
        {
            await SubmitAsync(restartedA, "tenant-r", "rate-user", "rate-after-restart-1", BufferPlan(), cost: 1),
            await SubmitAsync(nodeB, "tenant-r", "rate-user", "rate-after-restart-2", BufferPlan(), cost: 1),
        };
        WriteLedger("rate-after-restart", limits, afterRestart, sampler: null);
        afterRestart.Should().OnlyContain(a => !a.Admitted && a.Dimension == ExecutionAdmissionDimension.Rate);

        // The restarted node is healthy: another principal's window is untouched.
        (await SubmitAsync(restartedA, "tenant-r", "other-user", "other-user-1", BufferPlan(), cost: 1))
            .Admitted.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Restart recovery and convergence
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task RestartedNode_CountsWorkAdmittedBeforeRestart_AndCountsConvergeAfterCompletion()
    {
        var limits = Limits(global: 100, perPartition: 2);
        var nodeA = await CreateNodeAsync("node-a", limits);
        var nodeB = await CreateNodeAsync("node-b", limits);

        var first = await SubmitAsync(nodeA, "tenant-a", "alice", "restart-1", BufferPlan(), cost: 1);
        var second = await SubmitAsync(nodeB, "tenant-a", "alice", "restart-2", BufferPlan(), cost: 1);
        first.Admitted.Should().BeTrue();
        second.Admitted.Should().BeTrue();

        await RestartAsync(nodeA);
        var restartedA = await CreateNodeAsync("node-a-restarted", limits);

        var capped = await SubmitAsync(restartedA, "tenant-a", "alice", "restart-3", BufferPlan(), cost: 1);
        capped.Admitted.Should().BeFalse("the restarted node counts the work admitted before it restarted");
        capped.Dimension.Should().Be(ExecutionAdmissionDimension.Concurrency);

        // Completion and cancellation remove the jobs from the active set every node counts.
        await TransitionAsync(first.JobId!, ExecutionJobStatus.Succeeded);
        await TransitionAsync(second.JobId!, ExecutionJobStatus.Cancelled);
        (await _observerStore.ListActiveAsync()).Should().BeEmpty();

        // No leaked charge: the full partition budget is available again on both nodes.
        (await SubmitAsync(restartedA, "tenant-a", "alice", "restart-4", BufferPlan(), cost: 1)).Admitted.Should().BeTrue();
        (await SubmitAsync(nodeB, "tenant-a", "alice", "restart-5", BufferPlan(), cost: 1)).Admitted.Should().BeTrue();
        (await SubmitAsync(nodeB, "tenant-a", "alice", "restart-6", BufferPlan(), cost: 1)).Admitted.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmissionFailureAfterAdmission_ReleasesItsChargeOnEveryNode()
    {
        var limits = Limits(global: 100, perPartition: 1);
        var failingProgress = Substitute.For<IUniversalProgressStore>();
        failingProgress
            .SetProgressAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(
                _ => Task.FromException(new InvalidOperationException("progress store unavailable")),
                _ => Task.CompletedTask);
        var nodeA = await CreateNodeAsync("node-a", limits, progress: failingProgress);
        var nodeB = await CreateNodeAsync("node-b", limits);

        var failure = async () => await nodeA.Service.SubmitJobAsync(BufferPlan(), "fails-after-admission", Principal("tenant-a", "alice"));
        await failure.Should().ThrowAsync<InvalidOperationException>();

        var rolledBack = await _observerStore.GetAsync(GeoprocessingJobService.CreateJobId("fails-after-admission"));
        rolledBack.Should().NotBeNull();
        rolledBack!.Status.Should().Be(ExecutionJobStatus.Failed);
        (await _observerStore.ListActiveAsync()).Should().BeEmpty();

        // The one-slot partition was released by the rollback, so another node admits immediately.
        (await SubmitAsync(nodeB, "tenant-a", "alice", "after-rollback", BufferPlan(), cost: 1)).Admitted.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Shared lease failure modes: crashed holder, lost lease
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task NodeDiesHoldingTheAdmissionLease_OthersRejectWithRetryAfterUntilTheLeaseExpires()
    {
        var limits = Limits(global: 100, perPartition: 100);
        limits.SharedLeaseAcquireTimeoutMilliseconds = 300;
        var node = await CreateNodeAsync("node-b", limits);

        var database = _observer.GetDatabase();
        (await database.LockTakeAsync(ExecutionAdmissionCoordinator.SharedLeaseKey, "crashed-node", TimeSpan.FromSeconds(2)))
            .Should().BeTrue();

        var blocked = await SubmitAsync(node, "tenant-a", "alice", "while-wedged", BufferPlan(), cost: 1);
        blocked.Admitted.Should().BeFalse();
        blocked.Dimension.Should().Be(ExecutionAdmissionDimension.Backpressure);
        blocked.PolicyRef.Should().Be(ExecutionAdmissionCoordinator.ContendedPolicyRef);
        blocked.RetryAfterSeconds.Should().Be(RetryAfterSeconds);
        (await _observerStore.GetAsync(GeoprocessingJobService.CreateJobId("while-wedged"))).Should().BeNull();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await database.KeyExistsAsync(ExecutionAdmissionCoordinator.SharedLeaseKey) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        (await SubmitAsync(node, "tenant-a", "alice", "after-expiry", BufferPlan(), cost: 1)).Admitted.Should().BeTrue();
        (await database.KeyExistsAsync(ExecutionAdmissionCoordinator.SharedLeaseKey)).Should().BeFalse(
            "a completed admission releases the lease instead of holding it for its TTL");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task LeaseLostBeforeCreate_RejectsWithoutCreatingAnything()
    {
        // Simulates a pause longer than the lease TTL: the lease disappears while this node is
        // evaluating, so another node may be admitting against the same snapshot.
        var limits = Limits(global: 100, perPartition: 100);
        var database = _observer.GetDatabase();
        var node = await CreateNodeAsync(
            "node-a",
            limits,
            wrapStore: inner => new InterceptingJobStore(inner)
            {
                AfterListActive = () => database.KeyDeleteAsync(ExecutionAdmissionCoordinator.SharedLeaseKey)
            });

        var lost = await SubmitAsync(node, "tenant-a", "alice", "lease-lost", BufferPlan(), cost: 1);

        lost.Admitted.Should().BeFalse();
        lost.Dimension.Should().Be(ExecutionAdmissionDimension.Backpressure);
        lost.PolicyRef.Should().Be(ExecutionAdmissionCoordinator.LeaseLostPolicyRef);
        lost.RetryAfterSeconds.Should().Be(RetryAfterSeconds);
        (await _observerStore.GetAsync(GeoprocessingJobService.CreateJobId("lease-lost"))).Should().BeNull();
        (await _observerStore.ListActiveAsync()).Should().BeEmpty();
        node.EnqueuedJobIds.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Idempotent replay across nodes
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task ConcurrentReplayOfOneKeyAcrossNodes_CreatesOneJobAndChargesOnce()
    {
        var limits = Limits(global: 100, perPartition: 100, rate: 2);
        var nodeA = await CreateNodeAsync("node-a", limits);
        var nodeB = await CreateNodeAsync("node-b", limits);

        var submissions = Enumerable.Range(0, 10)
            .Select(i => (Func<Task<Attempt>>)(() => SubmitAsync(
                i % 2 == 0 ? nodeA : nodeB, "tenant-a", "alice", "one-key", BufferPlan(), cost: 1)))
            .ToList();

        var (attempts, sampler) = await BurstAsync(submissions);
        WriteLedger(nameof(ConcurrentReplayOfOneKeyAcrossNodes_CreatesOneJobAndChargesOnce), limits, attempts, sampler);

        attempts.Should().OnlyContain(a => a.Admitted);
        attempts.Select(a => a.JobId).Distinct().Should().ContainSingle();
        (await _observerStore.ListActiveAsync()).Should().ContainSingle();
        (nodeA.EnqueuedJobIds.Length + nodeB.EnqueuedJobIds.Length).Should().Be(1);

        // Exactly one of the two rate slots was charged for the whole replay storm.
        (await SubmitAsync(nodeA, "tenant-a", "alice", "second-key", BufferPlan(), cost: 1)).Admitted.Should().BeTrue();
        var third = await SubmitAsync(nodeB, "tenant-a", "alice", "third-key", BufferPlan(), cost: 1);
        third.Admitted.Should().BeFalse();
        third.Dimension.Should().Be(ExecutionAdmissionDimension.Rate);
    }

    // -----------------------------------------------------------------------
    // Cross-tenant nondisclosure on any node
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task CrossTenantProbesOnAnotherNode_DiscloseNeitherExistenceStateNorCancellation()
    {
        var limits = Limits(global: 100, perPartition: 100);
        var nodeA = await CreateNodeAsync("node-a", limits);
        var nodeB = await CreateNodeAsync("node-b", limits);
        var alice = Principal("tenant-a", "alice");
        var mallory = Principal("tenant-b", "mallory");

        var job = await nodeA.Service.SubmitJobAsync(BufferPlan(), "tenant-a-job", alice);
        var beforeProbes = await _observerStore.GetAsync(job.OperationId);
        beforeProbes.Should().NotBeNull();

        var probeExisting = async () => await nodeB.Service.GetJobAsync(job.OperationId, mallory);
        var probeMissing = async () => await nodeB.Service.GetJobAsync("job-that-never-existed", mallory);
        var existingFailure = await probeExisting.Should().ThrowAsync<GeoprocessingNotFoundException>();
        var missingFailure = await probeMissing.Should().ThrowAsync<GeoprocessingNotFoundException>();
        existingFailure.Which.Message.Replace(job.OperationId, "{id}", StringComparison.Ordinal)
            .Should().Be(missingFailure.Which.Message.Replace("job-that-never-existed", "{id}", StringComparison.Ordinal));

        var page = await nodeB.Service.ListJobsAsync(new GeoprocessingJobListFilter(), mallory);
        page.Items.Should().NotContain(listed => listed.OperationId == job.OperationId);

        var cancelProbe = async () => await nodeB.Service.CancelJobAsync(job.OperationId, mallory);
        await cancelProbe.Should().ThrowAsync<GeoprocessingNotFoundException>();

        var unchanged = await _observerStore.GetAsync(job.OperationId);
        unchanged!.Status.Should().Be(beforeProbes!.Status);
        unchanged.Version.Should().Be(beforeProbes.Version, "a foreign cancel probe must not write the record");
        (await nodeA.Service.GetJobAsync(job.OperationId, alice)).OperationId.Should().Be(job.OperationId);
    }

    // -----------------------------------------------------------------------
    // Harness negative control
    // -----------------------------------------------------------------------

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task NodeLocalGatesOnly_UnderTheSameInterleaving_Oversubscribe()
    {
        // Negative control: with only per-node gates (the pre-#3853 critical section), holding
        // each node's create until both nodes have evaluated admits two jobs against a global
        // limit of one. This proves the interleaving below really reproduces the race, so the
        // shared-lease counterpart passing is evidence rather than luck.
        var limits = Limits(global: 1, perPartition: 100);
        var rendezvous = new Rendezvous(parties: 2, TimeSpan.FromSeconds(3));
        var nodeA = await CreateNodeAsync("node-a", limits, sharedLease: false, wrapStore: rendezvous.Wrap);
        var nodeB = await CreateNodeAsync("node-b", limits, sharedLease: false, wrapStore: rendezvous.Wrap);

        var (attempts, sampler) = await BurstAsync(
        [
            () => SubmitAsync(nodeA, "tenant-a", "alice", "race-a", BufferPlan(), cost: 1),
            () => SubmitAsync(nodeB, "tenant-b", "bob", "race-b", BufferPlan(), cost: 1),
        ]);
        WriteLedger(nameof(NodeLocalGatesOnly_UnderTheSameInterleaving_Oversubscribe), limits, attempts, sampler);

        rendezvous.AllArrived.Should().BeTrue("both nodes evaluated admission before either created its record");
        attempts.Should().OnlyContain(a => a.Admitted);
        (await _observerStore.ListActiveAsync()).Should().HaveCount(2, "per-node gates oversubscribe a global limit of 1");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SharedLease_UnderTheSameInterleaving_AdmitsExactlyTheLimit()
    {
        var limits = Limits(global: 1, perPartition: 100);
        var rendezvous = new Rendezvous(parties: 2, TimeSpan.FromSeconds(1));
        var nodeA = await CreateNodeAsync("node-a", limits, wrapStore: rendezvous.Wrap);
        var nodeB = await CreateNodeAsync("node-b", limits, wrapStore: rendezvous.Wrap);

        var (attempts, sampler) = await BurstAsync(
        [
            () => SubmitAsync(nodeA, "tenant-a", "alice", "race-a", BufferPlan(), cost: 1),
            () => SubmitAsync(nodeB, "tenant-b", "bob", "race-b", BufferPlan(), cost: 1),
        ]);
        WriteLedger(nameof(SharedLease_UnderTheSameInterleaving_AdmitsExactlyTheLimit), limits, attempts, sampler);

        // The second node cannot evaluate until the first has created its record and released.
        rendezvous.AllArrived.Should().BeFalse();
        attempts.Count(a => a.Admitted).Should().Be(1);
        attempts.Single(a => !a.Admitted).Dimension.Should().Be(ExecutionAdmissionDimension.Backpressure);
        sampler.MaxGlobal.Should().Be(1);
        (await _observerStore.ListActiveAsync()).Should().ContainSingle();
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private static ExecutionAdmissionOptions Limits(
        int global,
        int perPartition,
        double cost = 1000,
        int rate = 1000) => new()
        {
            Enabled = true,
            MaxConcurrentJobsGlobal = global,
            MaxConcurrentJobsPerPartition = perPartition,
            MaxCostWeightPerPartition = cost,
            MaxSubmissionsPerWindow = rate,
            RateWindowSeconds = 60,
            DefaultRetryAfterSeconds = RetryAfterSeconds
        };

    private async Task<Node> CreateNodeAsync(
        string name,
        ExecutionAdmissionOptions limits,
        bool sharedLease = true,
        IUniversalProgressStore? progress = null,
        Func<IExecutionJobStore, IExecutionJobStore>? wrapStore = null)
    {
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        IExecutionJobStore store = new RedisExecutionJobStore(multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
        if (wrapStore is not null)
        {
            store = wrapStore(store);
        }

        var monitor = new StaticOptionsMonitor<ExecutionAdmissionOptions>(limits);
        var evaluator = new ExecutionAdmissionEvaluator(
            monitor,
            TimeProvider.System,
            NullLogger<ExecutionAdmissionEvaluator>.Instance,
            store,
            multiplexer);
        var coordinator = new ExecutionAdmissionCoordinator(
            monitor,
            NullLogger<ExecutionAdmissionCoordinator>.Instance,
            sharedLease ? multiplexer : null);

        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        approval
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());

        var enqueued = new ConcurrentQueue<string>();
        var queue = Substitute.For<IJobQueue>();
        queue
            .WhenForAnyArgs(q => q.EnqueueAsync(default!, default, default))
            .Do(call => enqueued.Enqueue(call.ArgAt<string>(0)));

        progress ??= Substitute.For<IUniversalProgressStore>();
        var service = new GeoprocessingJobService(
            progress,
            [Substitute.For<IJobCancellationNotifier>()],
            authorization,
            approval,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            ExecutorOptions,
            store,
            queue,
            admissionEvaluator: evaluator,
            admissionCoordinator: coordinator);

        var node = new Node(name, multiplexer, service, enqueued);
        _nodes.Add(node);
        return node;
    }

    private async Task RestartAsync(Node node)
    {
        _nodes.Remove(node);
        await node.DisposeAsync();
    }

    private static async Task<Attempt> SubmitAsync(
        Node node,
        string tenant,
        string user,
        string key,
        AnalysisPlan plan,
        double cost)
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            var job = await node.Service.SubmitJobAsync(plan, key, Principal(tenant, user));
            return new Attempt(node.Name, tenant, user, key, at, cost, true, job.OperationId,
                ExecutionAdmissionOutcome.Admitted, null, null, null);
        }
        catch (GeoprocessingAdmissionException ex)
        {
            return new Attempt(node.Name, tenant, user, key, at, cost, false, null,
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

    private async Task AssertRejectionsLeftNoTraceAsync(
        IReadOnlyList<Attempt> rejected,
        IReadOnlyList<Attempt> admitted)
    {
        foreach (var attempt in rejected)
        {
            // A rejected request leaves no job record under its idempotency key, so a later retry
            // of the same key is a fresh submission rather than a replay of a ghost.
            (await _observerStore.GetAsync(GeoprocessingJobService.CreateJobId(attempt.Key)))
                .Should().BeNull($"rejected key '{attempt.Key}' must not leave a job record");
        }

        var admittedIds = admitted.Select(a => a.JobId!).ToHashSet(StringComparer.Ordinal);
        var enqueued = _nodes.SelectMany(node => node.EnqueuedJobIds).ToList();
        enqueued.Should().OnlyContain(id => admittedIds.Contains(id), "only admitted jobs reach the queue");
    }

    private async Task TransitionAsync(string jobId, ExecutionJobStatus status)
    {
        var current = await _observerStore.GetAsync(jobId);
        current.Should().NotBeNull();
        (await _observerStore.TrySetAsync(current! with { Status = status, UpdatedAt = DateTimeOffset.UtcNow }))
            .Should().BeTrue();
    }

    private void WriteLedger(
        string scenario,
        ExecutionAdmissionOptions limits,
        IEnumerable<Attempt> attempts,
        ActiveSampler? sampler)
    {
        _output.WriteLine(
            $"scenario={scenario} limits: global={limits.MaxConcurrentJobsGlobal} partition={limits.MaxConcurrentJobsPerPartition} " +
            $"cost={limits.MaxCostWeightPerPartition.ToString(CultureInfo.InvariantCulture)} rate={limits.MaxSubmissionsPerWindow}/{limits.RateWindowSeconds}s");
        _output.WriteLine("node\ttenant\tprincipal\tat\tidempotencyKey\tcost\toutcome\tdimension\tpolicy\tretryAfter\tjobId");
        foreach (var a in attempts.OrderBy(a => a.At))
        {
            _output.WriteLine(string.Join('\t',
                a.Node, a.Tenant, a.User, a.At.ToString("O", CultureInfo.InvariantCulture), a.Key,
                a.Cost.ToString(CultureInfo.InvariantCulture), a.Outcome?.ToString() ?? "-",
                a.Dimension?.ToString() ?? "-", a.PolicyRef ?? "-",
                a.RetryAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-", a.JobId ?? "-"));
        }

        if (sampler is not null)
        {
            _output.WriteLine(
                $"observed: samples={sampler.Samples} maxGlobal={sampler.MaxGlobal} " +
                $"maxPartition={string.Join(',', sampler.MaxPerPartition.Select(p => $"{p.Key}:{p.Value}"))} " +
                $"maxCost={string.Join(',', sampler.MaxCostPerPartition.Select(p => $"{p.Key}:{p.Value.ToString(CultureInfo.InvariantCulture)}"))}");
        }
    }

    private static ClaimsPrincipal Principal(string tenant, string user)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user),
                new Claim(ClaimTypes.NameIdentifier, user),
                new Claim("tenant_id", tenant)
            ],
            "Test"));

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

    private static string ReadPartition(ExecutionJobRecord job)
        => job.Spec.Parameters.TryGetValue(ExecutionAdmissionEvaluator.PartitionKeyParameterKey, out var partition)
            ? partition
            : DefaultPartition;

    private static double ReadCost(ExecutionJobRecord job)
        => job.Spec.Parameters.TryGetValue(ExecutionAdmissionEvaluator.CostWeightParameterKey, out var raw)
            ? double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;

    private sealed record Attempt(
        string Node,
        string Tenant,
        string User,
        string Key,
        DateTimeOffset At,
        double Cost,
        bool Admitted,
        string? JobId,
        ExecutionAdmissionOutcome? Outcome,
        ExecutionAdmissionDimension? Dimension,
        string? PolicyRef,
        int? RetryAfterSeconds);

    private sealed class Node(
        string name,
        ConnectionMultiplexer multiplexer,
        GeoprocessingJobService service,
        ConcurrentQueue<string> enqueued) : IAsyncDisposable
    {
        public string Name { get; } = name;

        public GeoprocessingJobService Service { get; } = service;

        public string[] EnqueuedJobIds => enqueued.ToArray();

        public ValueTask DisposeAsync() => multiplexer.DisposeAsync();
    }

    /// <summary>
    /// Independent observer of the durable active set: the maximum global count and the maximum
    /// per-partition count and cost weight seen at any sample during a burst.
    /// </summary>
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
                foreach (var partition in active.GroupBy(ReadPartition))
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
    /// timeout passes), forcing all evaluations to complete before any record becomes visible.
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

    private sealed class InterceptingJobStore(IExecutionJobStore inner) : IExecutionJobStore
    {
        public Func<Task>? BeforeCreate { get; init; }

        public Func<Task>? AfterListActive { get; init; }

        public async Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (BeforeCreate is not null)
            {
                await BeforeCreate();
            }

            return await inner.TryCreateAsync(job, ttl, cancellationToken);
        }

        public async Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(
            ExecutionJobKind? kind = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            var active = await inner.ListActiveAsync(kind, limit, cancellationToken);
            if (AfterListActive is not null)
            {
                await AfterListActive();
            }

            return active;
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => inner.GetAsync(operationId, cancellationToken);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => inner.SetAsync(job, ttl, cancellationToken);

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => inner.TrySetAsync(job, ttl, cancellationToken);

        public Task<bool> TrySetIfLeaseOwnedAsync(
            ExecutionJobRecord job,
            string leaseOperationId,
            string leaseOwnerId,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => inner.TrySetIfLeaseOwnedAsync(job, leaseOperationId, leaseOwnerId, ttl, cancellationToken);

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => inner.QueryAsync(query, cancellationToken);

        public Task<bool> TryAcquireLeaseAsync(
            string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => inner.TryAcquireLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

        public Task<bool> RenewLeaseAsync(
            string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => inner.RenewLeaseAsync(operationId, ownerId, leaseDuration, cancellationToken);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => inner.ReleaseLeaseAsync(operationId, ownerId, cancellationToken);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// A dedicated Redis for this class: tests flush it between runs, so it must never be the
    /// process-wide shared container or an externally supplied server.
    /// </summary>
    public sealed class RedisServer : IAsyncLifetime
    {
        private RedisContainer? _container;

        public string ConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            _container = new RedisBuilder("redis:7.2-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        public async Task DisposeAsync()
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
    }
}
