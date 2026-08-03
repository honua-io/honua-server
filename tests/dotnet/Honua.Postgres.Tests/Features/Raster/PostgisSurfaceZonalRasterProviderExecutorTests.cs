// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Raster;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgisSurfaceZonalRasterProviderExecutorTests
{
    [Fact]
    public void Capabilities_IncompleteAdmissionAndPublicationPath_RemainUnavailable()
    {
        var executor = new PostgisSurfaceZonalRasterProviderExecutor();

        executor.Capabilities.Select(capability => capability.Variant.ProcessId)
            .Should().Equal(
                PostgisSurfaceZonalExecutionContract.SlopeProcessId,
                PostgisSurfaceZonalExecutionContract.AspectProcessId,
                PostgisSurfaceZonalExecutionContract.HillshadeProcessId,
                PostgisSurfaceZonalExecutionContract.ZonalStatisticsProcessId);
        executor.Capabilities.Should().AllSatisfy(capability =>
        {
            capability.ProviderId.Should().Be(PostgisSurfaceZonalRasterProviderExecutor.ProviderId);
            capability.Engine.Should().Be(RasterEngine.Postgis);
            capability.Variant.SemanticVersion.Should()
                .Be(PostgisSurfaceZonalExecutionContract.SemanticVersion);
            capability.Variant.ImplementationVersion.Should()
                .Be($"honua.postgis.{capability.Variant.ProcessId}@1.0.0");
            capability.PolicyVersion.Should().Be(PostgisSurfaceZonalRasterProviderExecutor.PolicyVersion);
            capability.Availability.Should().Be(RasterProviderAvailability.Unavailable);
            capability.UnavailabilityReason.Should()
                .Be(PostgisSurfaceZonalRasterProviderExecutor.UnavailableReason);
        });
    }

    [Fact]
    public async Task ExecuteAsync_DirectCall_FailsClosedWithoutExecutingAPrimitive()
    {
        var executor = new PostgisSurfaceZonalRasterProviderExecutor();

        var result = await executor.ExecuteAsync(Request(), CancellationToken.None);

        result.Status.Should().Be(RasterProviderExecutionStatus.CapabilityUnavailable);
        result.ErrorCode.Should().Be(PostgisSurfaceZonalRasterProviderExecutor.UnavailableCode);
        result.ErrorMessage.Should().Be(PostgisSurfaceZonalRasterProviderExecutor.UnavailableReason);
        result.Outputs.Should().BeEmpty();
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public void AddPostgresRasterStore_RegistersUnavailableProviderDeclaration()
    {
        var services = new ServiceCollection();
        services.AddPostgresRasterStore();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IRasterProviderExecutor>().Should().ContainSingle()
            .Which.Should().BeOfType<PostgisSurfaceZonalRasterProviderExecutor>();
    }

    [Fact]
    public async Task ExecuteAsync_Slope_MapsSourceOutputParametersAndCancellation()
    {
        var surface = Substitute.For<ISurfaceAnalysisService>();
        var rasterStore = Substitute.For<IRasterStore>();
        var expected = SurfaceResult();
        using var cancellation = new CancellationTokenSource();
        var request = SurfaceRequest();
        surface.ComputeSlopeAsync(request, SlopeUnits.Percent, 2.5d, cancellation.Token)
            .Returns(expected);
        var dispatcher = new PostgisSurfaceZonalPrimitiveDispatcher(surface, rasterStore);

        var result = await dispatcher.ExecuteAsync(
            new PostgisSlopeBinding
            {
                ProcessId = PostgisSurfaceZonalExecutionContract.SlopeProcessId,
                Source = Source(),
                Units = SlopeUnits.Percent,
                ZFactor = 2.5d,
            },
            Output(),
            cancellation.Token);

        result.Surface.Should().Be(expected);
        result.ZonalStatistics.Should().BeNull();
        await surface.Received(1)
            .ComputeSlopeAsync(request, SlopeUnits.Percent, 2.5d, cancellation.Token);
        await surface.DidNotReceiveWithAnyArgs().ComputeAspectAsync(default, default);
        await rasterStore.DidNotReceiveWithAnyArgs().ComputeZonalStatisticsAsync(
            default,
            default,
            default,
            default,
            default!,
            default);
    }

    [Fact]
    public async Task ExecuteAsync_Aspect_MapsSourceOutputAndCancellation()
    {
        var surface = Substitute.For<ISurfaceAnalysisService>();
        var expected = SurfaceResult();
        using var cancellation = new CancellationTokenSource();
        var request = SurfaceRequest();
        surface.ComputeAspectAsync(request, cancellation.Token).Returns(expected);
        var dispatcher = new PostgisSurfaceZonalPrimitiveDispatcher(
            surface,
            Substitute.For<IRasterStore>());

        var result = await dispatcher.ExecuteAsync(
            new PostgisAspectBinding
            {
                ProcessId = PostgisSurfaceZonalExecutionContract.AspectProcessId,
                Source = Source(),
            },
            Output(),
            cancellation.Token);

        result.Surface.Should().Be(expected);
        await surface.Received(1).ComputeAspectAsync(request, cancellation.Token);
    }

    [Fact]
    public async Task ExecuteAsync_Hillshade_MapsSourceOutputParametersAndCancellation()
    {
        var surface = Substitute.For<ISurfaceAnalysisService>();
        var expected = SurfaceResult();
        using var cancellation = new CancellationTokenSource();
        var request = SurfaceRequest();
        surface.ComputeHillshadeAsync(request, 270d, 30d, 1.75d, cancellation.Token)
            .Returns(expected);
        var dispatcher = new PostgisSurfaceZonalPrimitiveDispatcher(
            surface,
            Substitute.For<IRasterStore>());

        var result = await dispatcher.ExecuteAsync(
            new PostgisHillshadeBinding
            {
                ProcessId = PostgisSurfaceZonalExecutionContract.HillshadeProcessId,
                Source = Source(),
                AzimuthDegrees = 270d,
                AltitudeDegrees = 30d,
                ZFactor = 1.75d,
            },
            Output(),
            cancellation.Token);

        result.Surface.Should().Be(expected);
        await surface.Received(1)
            .ComputeHillshadeAsync(request, 270d, 30d, 1.75d, cancellation.Token);
    }

    [Fact]
    public async Task ExecuteAsync_ZonalStatistics_MapsSourceZonesBandStatisticsAndCancellation()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        var statistics = Array.AsReadOnly(new[] { "count", "mean", "max" });
        var expected = new[]
        {
            new RasterZonalStatisticsRow
            {
                ZoneFeatureId = 97,
                Band = 3,
                PixelCount = 42,
                Stats = new Dictionary<string, double?> { ["mean"] = 11.5d },
            },
        };
        using var cancellation = new CancellationTokenSource();
        rasterStore.ComputeZonalStatisticsAsync(41, 9001, 52, 3, statistics, cancellation.Token)
            .Returns(expected);
        var dispatcher = new PostgisSurfaceZonalPrimitiveDispatcher(
            Substitute.For<ISurfaceAnalysisService>(),
            rasterStore);

        var result = await dispatcher.ExecuteAsync(
            new PostgisZonalStatisticsBinding
            {
                ProcessId = PostgisSurfaceZonalExecutionContract.ZonalStatisticsProcessId,
                Source = Source(),
                ZonesLayerId = 52,
                Band = 3,
                Statistics = statistics,
            },
            surfaceOutput: null,
            cancellation.Token);

        result.Surface.Should().BeNull();
        result.ZonalStatistics.Should().Equal(expected);
        await rasterStore.Received(1)
            .ComputeZonalStatisticsAsync(41, 9001, 52, 3, statistics, cancellation.Token);
    }

    [Fact]
    public async Task ExecuteAsync_SurfaceWithoutPreparedOutput_RefusesBeforePrimitiveCall()
    {
        var surface = Substitute.For<ISurfaceAnalysisService>();
        var dispatcher = new PostgisSurfaceZonalPrimitiveDispatcher(
            surface,
            Substitute.For<IRasterStore>());

        var act = async () => await dispatcher.ExecuteAsync(
            new PostgisAspectBinding
            {
                ProcessId = PostgisSurfaceZonalExecutionContract.AspectProcessId,
                Source = Source(),
            },
            surfaceOutput: null,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*execution-owned prepared output target*");
        await surface.DidNotReceiveWithAnyArgs().ComputeAspectAsync(default, default);
    }

    private static PostgisRasterSourceDescriptor Source() => new()
    {
        LayerId = 41,
        RasterId = 9001,
        Version = "catalog-version-7",
        Content = new RasterContentIdentity
        {
            SizeBytes = 8192,
            MediaType = "application/vnd.postgis-raster",
        },
        SecurityContext = new RasterSecurityContextReference
        {
            TenantId = "tenant-a",
            AuthorizationSnapshotReference = "untrusted-hint",
        },
    };

    private static PostgisPreparedSurfaceOutput Output() => new(73, "attempt-owned-output");

    private static SurfaceAnalysisRequest SurfaceRequest() => new()
    {
        SourceLayerId = 41,
        SourceRasterId = 9001,
        OutputLayerId = 73,
        OutputName = "attempt-owned-output",
    };

    private static SurfaceAnalysisResult SurfaceResult() => new()
    {
        LayerId = 73,
        RasterId = 10002,
        Width = 128,
        Height = 64,
        Srid = 4326,
    };

    private static RasterProviderExecutionRequest Request() => new()
    {
        OperationId = "operation-1",
        Attempt = 1,
        TenantId = "tenant-a",
        Decision = new RasterExecutionDecision
        {
            ProcessId = PostgisSurfaceZonalExecutionContract.SlopeProcessId,
            Engine = RasterEngine.Postgis,
            ProviderId = PostgisSurfaceZonalRasterProviderExecutor.ProviderId,
            ProviderPolicyVersion = PostgisSurfaceZonalRasterProviderExecutor.PolicyVersion,
            Placement = RasterExecutionPlacement.DurablePostgis,
            InputResidencies = [RasterInputResidency.Postgis],
            OutputSink = RasterOutputSink.Postgis,
            Cost = new RasterCostEstimate
            {
                ProcessId = PostgisSurfaceZonalExecutionContract.SlopeProcessId,
                Engine = RasterEngine.Postgis,
                SourceCount = 1,
                BandCount = 1,
                ZoneCount = 0,
                InputPixels = 1,
                OutputPixels = 1,
                DecodedBytes = 1,
                ExpectedScratchBytes = 1,
                ExpectedDatabaseWork = 1,
                UnknownInputs = [],
                RequestExecutionAllowed = false,
            },
            SemanticVersion = PostgisSurfaceZonalExecutionContract.SemanticVersion,
            ImplementationVersion = "honua.postgis.surface.slope@1.0.0",
            ReasonCode = "test",
            Reason = "test",
            PolicyRef = "test-policy",
            ConfigurationVersion = "test-config",
            HealthVersion = "test-health",
        },
        Parameters = new Dictionary<string, string>(),
    };
}
