// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

public sealed partial class RoutingJobExecutorTests
{
    [UnitTheory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"lon\":0}")]
    [InlineData(true, "{\"lon\":0}")]
    [InlineData(false, "{\"lat\":0}")]
    [InlineData(true, "{\"lat\":0}")]
    [InlineData(false, "{\"lon\":null,\"lat\":0}")]
    [InlineData(true, "{\"lon\":0,\"lat\":null}")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    public async Task CanonicalRequest_IncompletePoint_DoesNotInvokeProviderOrPublish(bool serviceArea, string point)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities
        {
            SupportedTravelModes = ["driving"],
        });
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var request = serviceArea ? "{\"facilities\":[" + point + "],\"breaks\":[1]}"
            : "{\"stops\":[" + point + ",{\"lon\":1,\"lat\":1}]}";

        var result = await executor.ExecuteAsync(Job(serviceArea ? RoutingProcessDefinitions.ServiceArea : RoutingProcessDefinitions.Route,
            request), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain("invalid");
        await provider.DidNotReceiveWithAnyArgs().SolveRouteAsync(default!, default);
        await provider.DidNotReceiveWithAnyArgs().SolveServiceAreaAsync(default!, default);
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
                && value.OutSrid == expectedOutSrid && value.Barriers.Count == 0
                && value.TravelDirection == ServiceAreaTravelDirection.FromFacility), Arg.Any<CancellationToken>());
        }
        else
        {
            await provider.Received(1).SolveRouteAsync(Arg.Is<RouteSolveRequest>(value => value.InSrid == expectedInSrid
                && value.OutSrid == expectedOutSrid && value.Barriers.Count == 0
                && value.TravelProfile == "driving"), Arg.Any<CancellationToken>());
        }
    }

    [UnitTheory]
    [InlineData(false, "\"inSrid\":null", "invalid")]
    [InlineData(true, "\"inSrid\":null", "invalid")]
    [InlineData(false, "\"barriers\":null", "barriers")]
    [InlineData(true, "\"barriers\":null", "barriers")]
    [InlineData(false, "\"inSrid\":0", "spatial references")]
    [InlineData(true, "\"outSrid\":0", "spatial references")]
    [InlineData(false, "\"travelProfile\":\"flying\"", "travelMode")]
    [InlineData(false, "\"travelMode\":\"driving\"", "travelMode")]
    public async Task CanonicalRequest_ExplicitInvalidOptions_AreNotReplacedWithDefaults(
        bool serviceArea, string option, string expectedError)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var required = serviceArea ? "\"facilities\":[{\"lon\":0,\"lat\":0}],\"breaks\":[1]"
            : "\"stops\":[{\"lon\":0,\"lat\":0},{\"lon\":1,\"lat\":1}]";

        var result = await executor.ExecuteAsync(Job(serviceArea ? RoutingProcessDefinitions.ServiceArea : RoutingProcessDefinitions.Route,
            "{" + required + "," + option + "}"), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.IsRetryable.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedError);
        await provider.DidNotReceiveWithAnyArgs().SolveRouteAsync(default!, default);
        await provider.DidNotReceiveWithAnyArgs().SolveServiceAreaAsync(default!, default);
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTheory]
    [InlineData("driving")]
    [InlineData("DRIVING")]
    public async Task CanonicalRequest_ProviderWithoutNamedModes_UsesDefaultWeights(string profile)
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteSolveResult("", 0, 0, []));
        using var services = new ServiceCollection().AddOptions().AddSingleton(provider).BuildServiceProvider();
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<RoutingJobExecutor>.Instance);
        var request = new RouteSolveRequest([new(0, 0), new(1, 1)], profile);

        var result = await executor.ExecuteAsync(Job(RoutingProcessDefinitions.Route,
            System.Text.Json.JsonSerializer.Serialize(request, RoutingJobJsonContext.Default.RouteSolveRequest)),
            Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        await provider.Received(1).SolveRouteAsync(Arg.Is<RouteSolveRequest>(value => value.TravelMode == null
            && value.TravelProfile == profile), Arg.Any<CancellationToken>());
    }
}
