// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed class RoutingJobExecutorTests
{
    [UnitTheory]
    [InlineData("{\"stops\":[]}", "input points")]
    [InlineData("{\"stops\":null}", "input points")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"inSrid\":4326,\"outSrid\":4326,\"barriers\":[],\"travelMode\":\"flying\"}", "travelMode")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"inSrid\":4326,\"outSrid\":4326,\"barriers\":[],\"travelProfile\":\"flying\"}", "travelMode")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"inSrid\":0}", "spatial references")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"outSrid\":0}", "spatial references")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"inSrid\":null}", "invalid")]
    [InlineData("{\"stops\":[{\"lon\":1,\"lat\":1},{\"lon\":2,\"lat\":2}],\"barriers\":null}", "barriers")]
    [InlineData("invalid json", "invalid")]
    public async Task CanonicalRequest_InvalidRoute_DoesNotInvokeProviderOrPublish(string request, string expectedError)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();

        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route, request), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedError);
        await provider.DidNotReceiveWithAnyArgs().SolveRouteAsync(default!, default);
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTheory]
    [InlineData(false, "{\"stops\":[{\"lon\":0,\"lat\":0},{\"lon\":1,\"lat\":1}]}", 4326, 4326)]
    [InlineData(true, "{\"facilities\":[{\"lon\":0,\"lat\":0}],\"breaks\":[1]}", 4326, 4326)]
    [InlineData(false, "{\"stops\":[{\"lon\":0,\"lat\":0},{\"lon\":1,\"lat\":1}],\"outSrid\":3857}", 3857, 3857)]
    [InlineData(true, "{\"facilities\":[{\"lon\":0,\"lat\":0}],\"breaks\":[1],\"outSrid\":3857}", 3857, 3857)]
    [InlineData(false, "{\"STOPS\":[{\"lon\":0,\"lat\":0},{\"lon\":1,\"lat\":1}],\"INSRID\":4326,\"OUTSRID\":3857}", 4326, 3857)]
    [InlineData(true, "{\"FACILITIES\":[{\"lon\":0,\"lat\":0}],\"BREAKS\":[1],\"INSRID\":4326,\"OUTSRID\":3857}", 4326, 3857)]
    public async Task CanonicalRequest_OptionalProperties_PreserveDefaultsAndExplicitSpatialReferences(
        bool serviceArea, string request, int expectedInSrid, int expectedOutSrid)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities
        {
            SupportedTravelModes = ["driving"],
        });
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteSolveResult("", 0, 0, []));
        provider.SolveServiceAreaAsync(Arg.Any<ServiceAreaSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServiceAreaSolveResult([]));
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);

        var result = await executor.ExecuteAsync(Job(serviceArea ? RoutingProcessDefinitions.ServiceArea : RoutingProcessDefinitions.Route,
            request), Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        if (serviceArea)
        {
            await provider.Received(1).SolveServiceAreaAsync(Arg.Is<ServiceAreaSolveRequest>(value => value.InSrid == expectedInSrid
                && value.OutSrid == expectedOutSrid && value.Barriers.Count == 0), Arg.Any<CancellationToken>());
        }
        else
        {
            await provider.Received(1).SolveRouteAsync(Arg.Is<RouteSolveRequest>(value => value.InSrid == expectedInSrid
                && value.OutSrid == expectedOutSrid && value.Barriers.Count == 0), Arg.Any<CancellationToken>());
        }
    }

    [UnitTest]
    public async Task CanonicalRequest_StopLimit_IsEnforcedAtWorker()
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        using var services = new ServiceCollection().AddOptions().Configure<RoutingConfiguration>(value => value.MaxStops = 2)
            .AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var request = new RouteSolveRequest([new(1, 1), new(2, 2), new(3, 3)]);
        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route,
            JsonSerializer.Serialize(request, RoutingJobJsonContext.Default.RouteSolveRequest)), Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        await provider.DidNotReceiveWithAnyArgs().SolveRouteAsync(default!, default);
    }

    [UnitTest]
    public async Task CanonicalRequest_NoRoute_PublishesFalseSolveResult()
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>()).Returns(new RouteSolveResult("", 0, 0, []));
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var outputs = new List<string>();
        context.When(value => value.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(call => outputs.Add(call.ArgAt<string>(0)));
        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route,
            JsonSerializer.Serialize(new RouteSolveRequest([new(1, 1), new(2, 2)]), RoutingJobJsonContext.Default.RouteSolveRequest)), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        outputs.Should().HaveCount(2);
        using var features = JsonDocument.Parse(Convert.FromBase64String(outputs[0].Split(',')[1]));
        features.RootElement.GetProperty("features").GetArrayLength().Should().Be(0);
        using var solved = JsonDocument.Parse(Convert.FromBase64String(outputs[1].Split(',')[1]));
        solved.RootElement.GetBoolean().Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("[]")]
    [InlineData("[0]")]
    [InlineData("[5,2]")]
    [InlineData("[2,2]")]
    public async Task CanonicalRequest_InvalidServiceAreaBreaks_DoesNotInvokeProviderOrPublish(string breaks)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var request = "{\"facilities\":[{\"lon\":1,\"lat\":1}],\"breaks\":" + breaks + "}";

        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.ServiceArea, request), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("breaks");
        result.IsRetryable.Should().BeFalse();
        await provider.DidNotReceiveWithAnyArgs().SolveServiceAreaAsync(default!, default);
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task CanonicalRequest_CanceledAfterProviderReturns_PublishesNoArtifacts()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return new RouteSolveResult("{\"type\":\"LineString\",\"coordinates\":[[1,1],[2,2]]}", 1, 1, []);
        });
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var request = JsonSerializer.Serialize(new RouteSolveRequest([new(1, 1), new(2, 2)]), RoutingJobJsonContext.Default.RouteSolveRequest);

        var execute = () => executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route, request), context, cancellation.Token);

        await execute.Should().ThrowAsync<OperationCanceledException>();
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTheory]
    [InlineData(false, RouteBarrierKind.Point, "not geojson")]
    [InlineData(true, RouteBarrierKind.Point, "not geojson")]
    [InlineData(false, RouteBarrierKind.Point, "null")]
    [InlineData(false, RouteBarrierKind.Point, "{\"type\":\"Point\",\"coordinates\":[]}")]
    [InlineData(false, RouteBarrierKind.Point, "{\"type\":\"Point\",\"coordinates\":[1e999,0]}")]
    [InlineData(false, RouteBarrierKind.Point, "{\"type\":\"LineString\",\"coordinates\":[[0,0],[1,1]]}")]
    [InlineData(true, RouteBarrierKind.Polygon, "{\"type\":\"Point\",\"coordinates\":[0,0]}")]
    [InlineData(false, RouteBarrierKind.Line, "{\"type\":\"LineString\",\"coordinates\":[[0,0]]}")]
    [InlineData(false, RouteBarrierKind.Line, "{\"type\":\"MultiLineString\",\"coordinates\":[]}")]
    [InlineData(false, RouteBarrierKind.Polygon, "{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[0,0]]]}")]
    [InlineData(false, RouteBarrierKind.Polygon, "{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[1,1],[0,1]]]}")]
    [InlineData(false, RouteBarrierKind.Polygon, "{\"type\":\"MultiPolygon\",\"coordinates\":[]}")]
    public async Task CanonicalRequest_MalformedBarrier_IsTerminalBeforeProviderDispatch(
        bool serviceArea, RouteBarrierKind kind, string geometry)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities
        {
            SupportedTravelModes = ["driving"],
            SupportedBarrierKinds = [RouteBarrierKind.Point, RouteBarrierKind.Line, RouteBarrierKind.Polygon],
        });
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var barriers = new[] { new RouteBarrier(kind, geometry) };
        var request = serviceArea
            ? JsonSerializer.Serialize(new ServiceAreaSolveRequest([new(0, 0)], [1]) { Barriers = barriers },
                RoutingJobJsonContext.Default.ServiceAreaSolveRequest)
            : JsonSerializer.Serialize(new RouteSolveRequest([new(0, 0), new(1, 1)]) { Barriers = barriers },
                RoutingJobJsonContext.Default.RouteSolveRequest);

        var result = await executor.ExecuteAsync(Job(serviceArea ? RoutingProcessDefinitions.ServiceArea : RoutingProcessDefinitions.Route,
            request), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("barrier");
        await provider.DidNotReceiveWithAnyArgs().SolveRouteAsync(default!, default);
        await provider.DidNotReceiveWithAnyArgs().SolveServiceAreaAsync(default!, default);
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTheory]
    [InlineData(RouteBarrierKind.Point, "{\"type\":\"Point\",\"coordinates\":[0,0]}")]
    [InlineData(RouteBarrierKind.Point, "{\"type\":\"MultiPoint\",\"coordinates\":[[0,0],[1,1]]}")]
    [InlineData(RouteBarrierKind.Line, "{\"type\":\"LineString\",\"coordinates\":[[0,0],[1,1]]}")]
    [InlineData(RouteBarrierKind.Line, "{\"type\":\"MultiLineString\",\"coordinates\":[[[0,0],[1,1]]]}")]
    [InlineData(RouteBarrierKind.Polygon, "{\"type\":\"Polygon\",\"coordinates\":[[[0,0],[1,0],[1,1],[0,0]]]}")]
    [InlineData(RouteBarrierKind.Polygon, "{\"type\":\"MultiPolygon\",\"coordinates\":[[[[0,0],[1,0],[1,1],[0,0]]]]}")]
    public async Task CanonicalRequest_ValidBarrierAndExplicitTravelMode_ReachProvider(
        RouteBarrierKind kind, string geometry)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities
        {
            SupportedTravelModes = ["walking"],
            SupportedBarrierKinds = [RouteBarrierKind.Point, RouteBarrierKind.Line, RouteBarrierKind.Polygon],
        });
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteSolveResult("", 0, 0, []));
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var request = new RouteSolveRequest([new(0, 0), new(1, 1)], "unused-profile")
        {
            TravelMode = "walking",
            Barriers = [new RouteBarrier(kind, geometry)],
        };

        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route,
            JsonSerializer.Serialize(request, RoutingJobJsonContext.Default.RouteSolveRequest)),
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await provider.Received(1).SolveRouteAsync(Arg.Is<RouteSolveRequest>(value => value.TravelMode == "walking"
            && value.Barriers.Count == 1 && value.Barriers[0].GeometryGeoJson == geometry), Arg.Any<CancellationToken>());
    }

    private static ExecutionJobRecord Job(string processId, string request) => new()
    {
        OperationId = "routing-worker-test",
        Status = ExecutionJobStatus.Running,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "geoprocessing:routing",
            Parameters = new Dictionary<string, string>
            {
                [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
                [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.request"] = request,
            },
        },
    };
}
