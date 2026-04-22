// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for <see cref="ExecutionAdmissionEvaluator"/>, which gates
/// submission on backpressure, concurrency, rate, and cost dimensions.
/// Exercises each dimension individually plus the short-circuit evaluation
/// order so callers can rely on the first-failing-dimension contract.
/// </summary>
public sealed class ExecutionAdmissionEvaluatorTests
{
    private const string Principal = "alice";
    private const string Partition = "workspace-1";

    private readonly IExecutionJobStore _jobStore = Substitute.For<IExecutionJobStore>();
    private readonly FakeTimeProvider _time = new();

    public ExecutionAdmissionEvaluatorTests()
    {
        _jobStore
            .ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExecutionJobRecord>());
    }

    [Fact]
    public async Task Evaluate_WhenDisabled_Admits()
    {
        var options = new ExecutionAdmissionOptions { Enabled = false };
        var sut = CreateSut(options);

        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
        decision.DenyingDimension.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_WhenNoLimitsExceeded_Admits()
    {
        var sut = CreateSut(DefaultOptions());

        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
        decision.Snapshot.ActiveJobsGlobal.Should().Be(0);
        decision.Snapshot.ActiveJobsInPartition.Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // Backpressure — system-wide global limit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_GlobalLimitReached_DeniesBackpressure()
    {
        var options = DefaultOptions();
        options.MaxConcurrentJobsGlobal = 2;

        SeedActiveJobs(
            CreateActiveJob("other-1", partition: "other-partition", kind: ExecutionJobKind.TileCache),
            CreateActiveJob("other-2", partition: "other-partition", kind: ExecutionJobKind.ExtractTransformLoad));

        var sut = CreateSut(options);
        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Denied);
        decision.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Backpressure);
        decision.PolicyRef.Should().StartWith("backpressure:global:");
        decision.RetryAfterSeconds.Should().BePositive();
        decision.Snapshot.ActiveJobsGlobal.Should().Be(2);
    }

    // -----------------------------------------------------------------------
    // Concurrency — per-partition + kind limit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_PartitionLimitReached_DeniesConcurrency()
    {
        var options = DefaultOptions();
        options.MaxConcurrentJobsPerPartition = 2;
        options.MaxConcurrentJobsGlobal = 100;

        SeedActiveJobs(
            CreateActiveJob("gp-1", partition: Partition),
            CreateActiveJob("gp-2", partition: Partition),
            // A job in a different partition does not count against this partition's bucket.
            CreateActiveJob("gp-3", partition: "other-partition"));

        var sut = CreateSut(options);
        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Denied);
        decision.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Concurrency);
        decision.PolicyRef.Should().Contain("concurrency");
        decision.Snapshot.ActiveJobsInPartition.Should().Be(2);
        decision.Snapshot.ActiveJobsGlobal.Should().Be(3);
    }

    [Fact]
    public async Task Evaluate_PartitionLimit_OnlyCountsMatchingKind()
    {
        var options = DefaultOptions();
        options.MaxConcurrentJobsPerPartition = 1;
        options.MaxConcurrentJobsGlobal = 100;

        // Active job in the same partition but a different kind must not contribute.
        SeedActiveJobs(CreateActiveJob("etl-1", partition: Partition, kind: ExecutionJobKind.ExtractTransformLoad));

        var sut = CreateSut(options);
        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
    }

    // -----------------------------------------------------------------------
    // Rate — per-principal sliding window
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_RateLimitReached_ThrottlesRate()
    {
        var options = DefaultOptions();
        options.MaxSubmissionsPerWindow = 2;

        var sut = CreateSut(options);

        (await sut.EvaluateAsync(CreateRequest())).Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
        (await sut.EvaluateAsync(CreateRequest())).Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);

        var third = await sut.EvaluateAsync(CreateRequest());

        third.Outcome.Should().Be(ExecutionAdmissionOutcome.Throttled);
        third.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Rate);
        third.PolicyRef.Should().Contain("rate");
        third.RetryAfterSeconds.Should().BePositive();
    }

    [Fact]
    public async Task Evaluate_RateWindow_ExpiresAfterTimeAdvances()
    {
        var options = DefaultOptions();
        options.MaxSubmissionsPerWindow = 1;
        options.RateWindowSeconds = 30;

        var sut = CreateSut(options);

        (await sut.EvaluateAsync(CreateRequest())).Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);

        // Within the window — throttled.
        (await sut.EvaluateAsync(CreateRequest())).Outcome.Should().Be(ExecutionAdmissionOutcome.Throttled);

        // After the window — admitted again.
        _time.Advance(TimeSpan.FromSeconds(31));
        (await sut.EvaluateAsync(CreateRequest())).Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
    }

    [Fact]
    public async Task Evaluate_AnonymousPrincipal_SkipsRateGate()
    {
        var options = DefaultOptions();
        options.MaxSubmissionsPerWindow = 1;

        var sut = CreateSut(options);

        // Anonymous principal (null) should bypass the rate gate entirely.
        var anonRequest = new ExecutionAdmissionRequest
        {
            JobKind = ExecutionJobKind.Geoprocessing,
            PartitionKey = Partition,
            PrincipalId = null,
            EstimatedCostWeight = 1.0
        };

        (await sut.EvaluateAsync(anonRequest)).Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
        (await sut.EvaluateAsync(anonRequest)).Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
    }

    // -----------------------------------------------------------------------
    // Cost — aggregate cost weight per partition
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_CostLimitExceeded_DeniesCost()
    {
        var options = DefaultOptions();
        options.MaxCostWeightPerPartition = 10.0;

        // Active cost weight of 8 + a requested weight of 3 = 11 > 10 limit.
        SeedActiveJobs(CreateActiveJob("gp-heavy", partition: Partition, costWeight: 8.0));

        var sut = CreateSut(options);

        var decision = await sut.EvaluateAsync(CreateRequest(costWeight: 3.0));

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Denied);
        decision.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Cost);
        decision.PolicyRef.Should().Contain("cost");
        decision.Snapshot.ActiveCostWeightInPartition.Should().BeApproximately(8.0, 0.001);
    }

    [Fact]
    public async Task Evaluate_CostAtLimit_Admits()
    {
        var options = DefaultOptions();
        options.MaxCostWeightPerPartition = 10.0;

        SeedActiveJobs(CreateActiveJob("gp-heavy", partition: Partition, costWeight: 7.0));

        var sut = CreateSut(options);
        var decision = await sut.EvaluateAsync(CreateRequest(costWeight: 3.0));

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
    }

    // -----------------------------------------------------------------------
    // Short-circuit evaluation order: Backpressure → Concurrency → Rate → Cost
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_WhenBackpressureAndConcurrencyBothExceeded_ReportsBackpressureFirst()
    {
        var options = DefaultOptions();
        options.MaxConcurrentJobsGlobal = 1;
        options.MaxConcurrentJobsPerPartition = 1;

        SeedActiveJobs(CreateActiveJob("gp-1", partition: Partition));

        var sut = CreateSut(options);
        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Backpressure);
    }

    [Fact]
    public async Task Evaluate_WhenConcurrencyAndRateBothExceeded_ReportsConcurrencyFirst()
    {
        var options = DefaultOptions();
        options.MaxConcurrentJobsPerPartition = 1;
        options.MaxSubmissionsPerWindow = 1;

        SeedActiveJobs(CreateActiveJob("gp-1", partition: Partition));

        var sut = CreateSut(options);

        // First request consumes the rate slot and also sees concurrency already at cap.
        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Concurrency);
    }

    [Fact]
    public async Task Evaluate_WhenRateAndCostBothExceeded_ReportsRateFirst()
    {
        var options = DefaultOptions();
        options.MaxSubmissionsPerWindow = 1;
        options.MaxCostWeightPerPartition = 2.0;

        SeedActiveJobs(CreateActiveJob("gp-1", partition: Partition, costWeight: 5.0));

        var sut = CreateSut(options);

        (await sut.EvaluateAsync(CreateRequest())).Outcome.Should().Be(ExecutionAdmissionOutcome.Denied);
        // That call denied on Cost (rate slot was consumed atomically with the claim).
        // Second call: both rate and cost would reject; rate is checked first.
        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.DenyingDimension.Should().Be(ExecutionAdmissionDimension.Rate);
    }

    // -----------------------------------------------------------------------
    // Job store missing — do not crash, skip concurrency/cost gates
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Evaluate_WithoutJobStore_SkipsBackpressureAndConcurrencyGates()
    {
        var options = DefaultOptions();
        options.MaxConcurrentJobsGlobal = 0;
        options.MaxConcurrentJobsPerPartition = 0;

        var sut = new ExecutionAdmissionEvaluator(
            new TestOptionsMonitor(options),
            _time,
            NullLogger<ExecutionAdmissionEvaluator>.Instance,
            jobStore: null);

        var decision = await sut.EvaluateAsync(CreateRequest());

        decision.Outcome.Should().Be(ExecutionAdmissionOutcome.Admitted);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private ExecutionAdmissionEvaluator CreateSut(ExecutionAdmissionOptions options)
        => new(
            new TestOptionsMonitor(options),
            _time,
            NullLogger<ExecutionAdmissionEvaluator>.Instance,
            _jobStore);

    private static ExecutionAdmissionOptions DefaultOptions() => new()
    {
        Enabled = true,
        MaxConcurrentJobsPerPartition = 100,
        MaxConcurrentJobsGlobal = 100,
        MaxSubmissionsPerWindow = 100,
        RateWindowSeconds = 60,
        MaxCostWeightPerPartition = 1000,
        DefaultRetryAfterSeconds = 10
    };

    private static ExecutionAdmissionRequest CreateRequest(double costWeight = 1.0) => new()
    {
        JobKind = ExecutionJobKind.Geoprocessing,
        PartitionKey = Partition,
        PrincipalId = Principal,
        EstimatedCostWeight = costWeight
    };

    private void SeedActiveJobs(params ExecutionJobRecord[] jobs)
    {
        _jobStore
            .ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<CancellationToken>())
            .Returns(jobs);
    }

    private static ExecutionJobRecord CreateActiveJob(
        string id,
        string partition,
        ExecutionJobKind kind = ExecutionJobKind.Geoprocessing,
        double costWeight = 1.0)
    {
        var parameters = new Dictionary<string, string>
        {
            [ExecutionAdmissionEvaluator.PartitionKeyParameterKey] = partition,
            [ExecutionAdmissionEvaluator.CostWeightParameterKey] =
                costWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        };

        return new ExecutionJobRecord
        {
            OperationId = id,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test-workload",
                Parameters = parameters
            }
        };
    }

    private sealed class TestOptionsMonitor(ExecutionAdmissionOptions value) : IOptionsMonitor<ExecutionAdmissionOptions>
    {
        public ExecutionAdmissionOptions CurrentValue => value;

        public ExecutionAdmissionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ExecutionAdmissionOptions, string?> listener) => null;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
