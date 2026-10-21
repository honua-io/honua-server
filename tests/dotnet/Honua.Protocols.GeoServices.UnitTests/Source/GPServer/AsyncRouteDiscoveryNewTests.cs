// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.NAServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed class AsyncRouteDiscoveryNewTests
{
    [UnitTest]
    public async Task AsyncRouteDiscovery_DoesNotShadowAnExistingCatalogNameOrStableId()
    {
        foreach (var byId in new[] { false, true })
        {
            var graph = new MetadataV2Graph
            {
                Services = [new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = byId ? "ROUTINGROUTE" : "unrelated-id",
                        Name = byId ? "Private service" : "routingroute",
                    },
                }],
            };
            using var services = new ServiceCollection()
                .AddSingleton<IProcessCatalog>(new BuiltInProcessCatalog())
                .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
                .BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            (await GPServerEndpoints.CanPublishAsyncRouteAsync(context, CancellationToken.None)).Should().BeFalse();
        }
        using var empty = new ServiceCollection()
            .AddSingleton<IProcessCatalog>(new BuiltInProcessCatalog())
            .AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(new MetadataV2Graph()))
            .BuildServiceProvider();
        (await GPServerEndpoints.CanPublishAsyncRouteAsync(new DefaultHttpContext { RequestServices = empty }, CancellationToken.None))
            .Should().BeTrue();
    }

    [UnitTest]
    public void FindRoutes_UsesDurableProcessMetadataWithoutChangingSynchronousNasMetadata()
    {
        var definition = new BuiltInProcessCatalog().GetProcess(RoutingProcessDefinitions.Route)!;
        var task = GPServerEndpoints.BuildTaskInfo("FindRoutes", definition);
        task.ExecutionType.Should().Be("esriExecutionTypeAsynchronous");
        task.Parameters!.Single(p => p.Name == "Stops").DataType.Should().Be("GPFeatureRecordSetLayer");
        task.Parameters!.Where(p => p.Direction == "esriGPParameterDirectionOutput").Select(p => p.Name)
            .Should().Equal("Output_Routes", "Output_Stops", "Solve_Succeeded");
        NAServerMetadata.BuildUtilityTaskInfo("FindRoutes")["executionType"]!.GetValue<string>()
            .Should().Be("esriExecutionTypeSynchronous");
    }

    [UnitTest]
    public void FindRoutes_RejectsUnsupportedStopReorderingInsteadOfAdvertisingAnOptimizedSolve()
    {
        var definition = RoutingProcessDefinitions.Create().Single(p => p.ProcessId == RoutingProcessDefinitions.Route);
        var translate = () => RoutingAsyncWebTools.Translate(definition,
            new Dictionary<string, string> { ["Reorder_Stops_to_Find_Optimal_Routes"] = "true" }, null);
        translate.Should().Throw<GeoprocessingValidationException>();
        RoutingAsyncWebTools.Translate(definition,
            new Dictionary<string, string> { ["Reorder_Stops_to_Find_Optimal_Routes"] = "false" }, null)
            .Should().NotContainKey("Reorder_Stops_to_Find_Optimal_Routes");
    }
}
