// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

public sealed class AsyncRouteJobNewTests
{
    [UnitTest]
    public async Task RouteJob_PreservesOrderedStopsAndPublishesRealProviderCostsThroughTheFence()
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities(SupportsRoute: true));
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteSolveResult("""{"type":"LineString","coordinates":[[0,0],[0.02,0.02]]}""", 1234, 4, []));
        using var services = new ServiceCollection().AddSingleton(provider).BuildServiceProvider();
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), monitor, Options.Create(new RoutingConfiguration()));
        var context = Substitute.For<IJobExecutionContext>();
        List<string> published = [];
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { published.Add(call.ArgAt<string>(0)); return true; });

        var result = await executor.ExecuteAsync(Job(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var request = (RouteSolveRequest)provider.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IRoutingProvider.SolveRouteAsync)).GetArguments()[0]!;
        request.Stops.Select(p => (p.Lon, p.Lat)).Should().Equal((0d, 0d), (0.02d, 0.02d));
        published.Should().HaveCount(3);
        using var routes = Decode(published[0]);
        var attributes = routes.RootElement.GetProperty("features")[0].GetProperty("properties");
        attributes.GetProperty("Total_Minutes").GetDouble().Should().Be(4);
        attributes.GetProperty("Total_Length").GetDouble().Should().Be(1234);
        using var stops = Decode(published[1]);
        stops.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("OBJECTID").GetInt32().Should().Be(17);
        stops.RootElement.GetProperty("features")[1].GetProperty("properties").GetProperty("Sequence").GetInt32().Should().Be(2);
        using var solved = Decode(published[2]);
        solved.RootElement.GetBoolean().Should().BeTrue();
    }

    [UnitTest]
    public async Task RouteJob_RejectsLostPublicationFenceWithoutPublishingRemainingOutputs()
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities(SupportsRoute: true));
        provider.SolveRouteAsync(Arg.Any<RouteSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RouteSolveResult("""{"type":"LineString","coordinates":[[0,0],[0.02,0.02]]}""", 1234, 4, []));
        using var services = new ServiceCollection().AddSingleton(provider).BuildServiceProvider();
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        var executor = new RoutingJobExecutor(services.GetRequiredService<IServiceScopeFactory>(), monitor, Options.Create(new RoutingConfiguration()));
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await executor.ExecuteAsync(Job(), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        await context.Received(1).TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static JsonDocument Decode(string uri) => JsonDocument.Parse(Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]));

    private static ExecutionJobRecord Job()
    {
        const string features = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[0,0]},"properties":{"OBJECTID":17,"Name":"Start"}},{"type":"Feature","geometry":{"type":"Point","coordinates":[0.02,0.02]},"properties":{"OBJECTID":23,"Name":"End"}}]}""";
        return new ExecutionJobRecord
        {
            OperationId = "async-route-new", Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local", WorkloadName = "geoprocessing:test",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = RoutingProcessDefinitions.Route,
                    ["protocolProcessId"] = RoutingProcessDefinitions.Route,
                    [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.stops"] = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(features)),
                },
            },
        };
    }
}
