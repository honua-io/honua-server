// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class RasterProviderExecutorContractTests
{
    [UnitTest]
    public void Build_ExactProviderSemanticImplementationAndPolicy_ProducesOneRoute()
    {
        var capability = Capability("raster.clip");
        var executor = new FakeExecutor(capability);

        var routes = RasterProviderExecutorRouteTable.Build([executor]);

        routes.Should().ContainSingle();
        routes[new RasterProviderRouteKey(
                RasterEngine.Postgis,
                "postgis",
                "raster.clip",
                "1.0.0",
                "honua.postgis.raster.clip@1.0.0",
                "postgis-raster-v1")]
            .Executor.Should().BeSameAs(executor);
    }

    [UnitTest]
    public void Build_DuplicateExactRoute_FailsComposition()
    {
        var capability = Capability("raster.clip");

        var act = () => RasterProviderExecutorRouteTable.Build(
            [new FakeExecutor(capability), new FakeExecutor(capability)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate raster provider route*raster.clip*");
    }

    [UnitTest]
    public void Build_NoProviderExecutor_FailsWorkerComposition()
    {
        var act = () => RasterProviderExecutorRouteTable.Build([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*requires at least one executable capability route*");
    }

    [UnitTest]
    public void Build_AvailableCapabilityWithReason_FailsComposition()
    {
        var capability = Capability("raster.clip") with
        {
            UnavailabilityReason = "contradictory",
        };

        var act = () => RasterProviderExecutorRouteTable.Build([new FakeExecutor(capability)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unavailability reason exactly when*");
    }

    [UnitTest]
    public void CreateForProviderCapabilities_RegisteredExactVariant_BecomesAvailable()
    {
        var registry = RasterEngineCapabilityRegistry.CreateForProviderCapabilities(
            [Capability("raster.clip")],
            RasterEngineCapabilityRegistry.DefaultGdalRasterInputFormatNames,
            RasterEngineCapabilityRegistry.DefaultGdalSkippedDriverNames);

        var postgis = registry.Find("raster.clip")!.Engines
            .Single(engine => engine.Engine == RasterEngine.Postgis);
        postgis.IsAvailable.Should().BeTrue();
        postgis.ProviderId.Should().Be("postgis");
        postgis.ProviderPolicyVersion.Should().Be("postgis-raster-v1");
        postgis.ImplementationVersion.Should().Be("honua.postgis.raster.clip@1.0.0");
    }

    [UnitTest]
    public void CreateForProviderCapabilities_UnhealthyVariant_IsRetryableUnavailable()
    {
        var registry = RasterEngineCapabilityRegistry.CreateForProviderCapabilities(
            [Capability("raster.clip") with
            {
                Availability = RasterProviderAvailability.Unhealthy,
                UnavailabilityReason = "PostGIS health probe is failing.",
            }],
            RasterEngineCapabilityRegistry.DefaultGdalRasterInputFormatNames,
            RasterEngineCapabilityRegistry.DefaultGdalSkippedDriverNames);

        var postgis = registry.Find("raster.clip")!.Engines
            .Single(engine => engine.Engine == RasterEngine.Postgis);
        postgis.IsAvailable.Should().BeFalse();
        postgis.UnavailabilityIsRetryable.Should().BeTrue();
        postgis.UnavailabilityReason.Should().Be("PostGIS health probe is failing.");
    }

    [UnitTest]
    public void CreateForProviderCapabilities_UnknownProcess_FailsConfiguration()
    {
        var act = () => RasterEngineCapabilityRegistry.CreateForProviderCapabilities(
            [Capability("raster.unknown")],
            RasterEngineCapabilityRegistry.DefaultGdalRasterInputFormatNames,
            RasterEngineCapabilityRegistry.DefaultGdalSkippedDriverNames);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown process*raster.unknown*");
    }

    private static RasterProviderCapability Capability(string processId) => new()
    {
        ProviderId = "postgis",
        Engine = RasterEngine.Postgis,
        Variant = new RasterSemanticVariant
        {
            ProcessId = processId,
            SemanticVersion = "1.0.0",
            ImplementationVersion = $"honua.postgis.{processId}@1.0.0",
        },
        PolicyVersion = "postgis-raster-v1",
        Availability = RasterProviderAvailability.Available,
    };

    private sealed class FakeExecutor(params RasterProviderCapability[] capabilities)
        : IRasterProviderExecutor
    {
        public IReadOnlyList<RasterProviderCapability> Capabilities { get; } = capabilities;

        public Task<RasterProviderExecutionResult> ExecuteAsync(
            RasterProviderExecutionRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
