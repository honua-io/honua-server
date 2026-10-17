// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed partial class GeoprocessingJobServiceTests
{
    [Theory]
    [InlineData(null, "local")]
    [InlineData("managed", "local")]
    [InlineData("native", "native")]
    [Trait("Tier", "Fast")]
    public async Task ResolveWorkload_MixedRuntimeImages_KeepsManagedLocalAndSelectsNativeWorker(string? required, string expected)
    {
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            RuntimeWorkload("local", null, "local"),
            RuntimeWorkload("native", "native", "honua-kubernetes-job"),
            RuntimeWorkload("managed", "managed", "honua-aws-batch"),
        });
        var dispatcher = new GeoprocessingJobDispatcher(
            NullLogger<GeoprocessingJobService>.Instance, DefaultExecutorOptions, _progressStore,
            workloadRegistry: registry);

        var selected = await dispatcher.ResolveWorkloadAsync(false, required, CancellationToken.None);

        selected!.WorkloadId.Should().Be(expected);
    }

    [UnitTest]
    public async Task ResolveWorkload_NativeWithOnlyManagedRemote_UsesLocalNativeConsumer()
    {
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            RuntimeWorkload("managed", null, "honua-aws-batch"),
            RuntimeWorkload("local", null, "local"),
        });
        var dispatcher = new GeoprocessingJobDispatcher(
            NullLogger<GeoprocessingJobService>.Instance, DefaultExecutorOptions, _progressStore,
            workloadRegistry: registry);

        var selected = await dispatcher.ResolveWorkloadAsync(false, "native", CancellationToken.None);

        selected!.Backend.Should().Be("local");
    }

    [UnitTest]
    public async Task ResolveWorkload_NoCompatibleImage_RejectsBeforeSubmission()
    {
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            RuntimeWorkload("managed", null, "honua-aws-batch"),
        });
        var dispatcher = new GeoprocessingJobDispatcher(
            NullLogger<GeoprocessingJobService>.Instance, DefaultExecutorOptions, _progressStore,
            workloadRegistry: registry);

        var act = () => dispatcher.ResolveWorkloadAsync(false, "native", CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>().WithMessage("*native*");
    }

    [UnitTest]
    public async Task ResolveWorkload_ConfirmedKeyedReplay_SkipsIncompatibleImageRejection()
    {
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            RuntimeWorkload("managed", "managed", "honua-aws-batch"),
        });
        var dispatcher = new GeoprocessingJobDispatcher(
            NullLogger<GeoprocessingJobService>.Instance, DefaultExecutorOptions, _progressStore,
            workloadRegistry: registry);
        var consulted = 0;

        var selected = await dispatcher.ResolveWorkloadAsync(
            false,
            "native",
            CancellationToken.None,
            _ =>
            {
                consulted++;
                return Task.FromResult(true);
            });

        selected.Should().BeNull();
        consulted.Should().Be(1);
    }

    [UnitTest]
    public async Task SubmitJob_KeyedReplay_ReturnsAcceptedJobWhenWorkloadBecomesIncompatible()
    {
        var store = new InMemoryExecutionJobStore();
        var workloads = new List<ExecutionJobDefinition>
        {
            RuntimeWorkload("local", null, "local"),
        };
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => workloads.ToArray());
        var service = CreateStoreBackedService(store, registry);
        var plan = NativeGdalPlan();
        var principal = CreateStablePrincipal();

        var first = await service.SubmitJobAsync(plan, "native-key", principal);

        workloads.Clear();
        workloads.Add(RuntimeWorkload("managed-remote", "managed", "honua-aws-batch"));

        var replay = await service.SubmitJobAsync(plan, "native-key", principal);
        replay.OperationId.Should().Be(first.OperationId);

        var act = () => service.SubmitJobAsync(plan, "other-key", principal);
        await act.Should().ThrowAsync<GeoprocessingValidationException>().WithMessage("*native*");
    }

    [UnitTest]
    public async Task ResolveWorkload_ServerlessManaged_RefusesLocalAndDoesNotSelectNative()
    {
        var dispatcher = Dispatcher(LocalAndNativeRegistry(), new FrozenHost());

        var act = () => dispatcher.ResolveWorkloadAsync(false, "managed", CancellationToken.None);

        var refusal = await act.Should().ThrowAsync<GeoprocessingStoreUnavailableException>();
        refusal.Which.ErrorCode.Should().Be("dependency-unavailable");
        refusal.Which.MissingDependency.Should().Be("long-lived-host");
        refusal.Which.HasDependencyReceipt.Should().BeTrue();
    }

    [UnitTest]
    public async Task ResolveWorkload_ServerlessManagedRemote_SelectsThatWorker()
    {
        var dispatcher = Dispatcher(MixedRegistry(), new FrozenHost());

        var selected = await dispatcher.ResolveWorkloadAsync(false, null, CancellationToken.None);

        selected!.WorkloadId.Should().Be("managed");
        selected.Backend.Should().Be("honua-aws-batch");
    }

    [UnitTest]
    public async Task ResolveWorkload_ServerlessNative_SelectsNativeWorker()
    {
        var dispatcher = Dispatcher(MixedRegistry(), new FrozenHost());

        var selected = await dispatcher.ResolveWorkloadAsync(false, "native", CancellationToken.None);

        selected!.WorkloadId.Should().Be("native");
    }

    [UnitTest]
    public async Task ResolveWorkload_LongLivedHost_ManagedWithOnlyNativeRemote_RejectsAsValidation()
    {
        var dispatcher = Dispatcher(NativeOnly());

        var act = () => dispatcher.ResolveWorkloadAsync(false, "managed", CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>().WithMessage("*managed*");
    }

    [UnitTest]
    public async Task ResolveWorkload_Serverless_ManagedWithOnlyNativeRemote_RefusesAsDependency()
    {
        var dispatcher = Dispatcher(NativeOnly(), new FrozenHost());

        var act = () => dispatcher.ResolveWorkloadAsync(false, "managed", CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingStoreUnavailableException>()
            .WithMessage("*background loop*");
    }

    [UnitTest]
    public async Task SubmitJob_ServerlessManagedBuffer_RefusesBeforeCreatingJob()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateStoreBackedService(store, LocalAndNativeRegistry(), new FrozenHost());

        var act = () => service.SubmitJobAsync(CreateValidPlan(), "serverless-buffer", CreateStablePrincipal());

        await act.Should().ThrowAsync<GeoprocessingStoreUnavailableException>();
        (await store.ListActiveAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task SubmitJob_ServerlessReplay_ReturnsJobAcceptedOnLongLivedHost()
    {
        var store = new InMemoryExecutionJobStore();
        var registry = LocalAndNativeRegistry();
        var principal = CreateStablePrincipal();
        var plan = CreateValidPlan();
        var accepted = await CreateStoreBackedService(store, registry)
            .SubmitJobAsync(plan, "buffer-key", principal);

        var replay = await CreateStoreBackedService(store, registry, new FrozenHost())
            .SubmitJobAsync(plan, "buffer-key", principal);
        replay.OperationId.Should().Be(accepted.OperationId);

        var act = () => CreateStoreBackedService(store, registry, new FrozenHost())
            .SubmitJobAsync(plan, "other-buffer", principal);
        await act.Should().ThrowAsync<GeoprocessingStoreUnavailableException>();
        (await store.ListActiveAsync()).Should().ContainSingle(job => job.OperationId == accepted.OperationId);
    }

    private GeoprocessingJobDispatcher Dispatcher(
        IExecutionJobDefinitionRegistry registry,
        IGeoprocessingHostRuntime? host = null)
        => new(
            NullLogger<GeoprocessingJobService>.Instance,
            DefaultExecutorOptions,
            _progressStore,
            workloadRegistry: registry)
        {
            HostRuntime = host ?? ProcessEnvironmentHostRuntime.Instance,
        };

    private static IExecutionJobDefinitionRegistry MixedRegistry()
    {
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(MixedWorkloads());
        return registry;
    }

    private static ExecutionJobDefinition[] MixedWorkloads() =>
    [
        RuntimeWorkload("local", null, "local"),
        RuntimeWorkload("native", "native", "honua-kubernetes-job"),
        RuntimeWorkload("managed", "managed", "honua-aws-batch"),
    ];

    private static IExecutionJobDefinitionRegistry LocalAndNativeRegistry()
    {
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            RuntimeWorkload("local", null, "local"),
            RuntimeWorkload("native", "native", "honua-kubernetes-job"),
        });
        return registry;
    }

    private static IExecutionJobDefinitionRegistry NativeOnly()
    {
        var registry = Substitute.For<IExecutionJobDefinitionRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            RuntimeWorkload("native", "native", "honua-kubernetes-job"),
        });
        return registry;
    }

    private sealed class FrozenHost : IGeoprocessingHostRuntime
    {
        public bool IsServerless => true;
    }

    private static AnalysisPlan NativeGdalPlan() => new()
    {
        PlanId = "plan-native",
        IntentId = "intent-native",
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

    private static ExecutionJobDefinition RuntimeWorkload(string id, string? profile, string backend)
        => new()
        {
            WorkloadId = id,
            WorkloadName = id,
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = backend,
            RuntimeProfile = profile,
        };
}

public sealed class ServerlessHostMarkerTests
{
    [Theory]
    [InlineData("AWS_LAMBDA_FUNCTION_NAME")]
    [InlineData("FUNCTIONS_WORKER_RUNTIME")]
    [InlineData("K_SERVICE")]
    [Trait("Tier", "Fast")]
    public void IsServerlessEnvironment_Marker_IsFrozen(string marker)
    {
        ProcessEnvironmentHostRuntime.IsServerlessEnvironment(name => name == marker ? "honua" : null)
            .Should().BeTrue();
    }

    [UnitTest]
    public void IsServerlessEnvironment_NoMarker_IsLongLived()
    {
        ProcessEnvironmentHostRuntime.IsServerlessEnvironment(_ => null).Should().BeFalse();
        ProcessEnvironmentHostRuntime.IsServerlessEnvironment(
            name => name == "AWS_LAMBDA_FUNCTION_NAME" ? "  " : null).Should().BeFalse();
    }
}
