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
    [InlineData(null, "managed")]
    [InlineData("managed", "managed")]
    [InlineData("native", "native")]
    [Trait("Tier", "Fast")]
    public async Task ResolveWorkload_MixedRuntimeImages_SelectsCompatibleRemote(string? required, string expected)
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
