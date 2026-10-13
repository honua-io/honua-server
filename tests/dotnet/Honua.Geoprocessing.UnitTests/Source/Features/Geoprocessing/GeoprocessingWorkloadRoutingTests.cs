// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
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
