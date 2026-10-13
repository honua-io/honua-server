// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Configuration;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Configuration;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Protocols.Ogc.Api.Maps;
using Honua.Protocols.Ogc.Api.Maps.Handlers;
using Honua.Protocols.Ogc.Api.Maps.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class OgcMapsTimeoutRegressionTests
{
    [Theory]
    [InlineData("GetDatasetMap", true)]
    [InlineData("GetCollectionMap", true)]
    [InlineData("GetStyledCollectionMap", true)]
    [InlineData("GetCollectionMapTileSets", true)]
    [InlineData("GetCollectionMapTileSet", true)]
    [InlineData("GetDatasetMap", false)]
    [InlineData("GetCollectionMap", false)]
    [InlineData("GetStyledCollectionMap", false)]
    [InlineData("GetCollectionMapTileSets", false)]
    [InlineData("GetCollectionMapTileSet", false)]
    public async Task MapEndpoint_CanceledRequest_ReachesGraphProvider(string method, bool deadline)
    {
        using var budget = new CancellationTokenSource();
        budget.Cancel();
        var context = new DefaultHttpContext();
        if (deadline)
        {
            context.Items["LimitsTimeoutToken"] = budget.Token;
        }
        else
        {
            context.RequestAborted = budget.Token;
        }
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        CancellationToken? observed = null;
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            observed = call.Arg<CancellationToken>();
            return new MetadataV2GraphSnapshot(
                new MetadataV2Graph(), "\"empty\"", DateTimeOffset.UnixEpoch);
        });
        var handler = new OgcMapsRenderingHandler(
            graphProvider,
            Substitute.For<IRasterMapRenderer>(),
            Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(graphProvider).BuildServiceProvider();
        context.RequestServices = services;
        var tilesHandler = new OgcMapsTileSetHandler(graphProvider, NullLogger<OgcMapsTileSetHandler>.Instance);
        var endpoint = typeof(OgcMapsEndpoints).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!;
        var token = context.RequestAborted;
        object?[] arguments = method switch
        {
            "GetCollectionMap" => ["test", new OgcMapRequest(), context, handler, token],
            "GetStyledCollectionMap" => ["test", "style", new OgcMapRequest(), context, handler, token],
            "GetCollectionMapTileSets" => ["test", context, tilesHandler, token],
            "GetCollectionMapTileSet" => ["test", "WebMercatorQuad", context, tilesHandler, token],
            _ => [new OgcMapRequest(), context, handler, token]
        };

        // Invoke the real route adapter with the token ASP.NET binds when the client
        // has not disconnected. The middleware's independent budget is already expired.
        await (Task<IResult>)endpoint.Invoke(null, arguments)!;

        observed.Should().NotBeNull();
        observed!.Value.IsCancellationRequested.Should().BeTrue(
            "Maps must propagate the configured end-to-end timeout, like Features and STAC");
    }

    [Fact]
    public async Task DatasetMap_ConfiguredDeadline_CancelsCooperativeRenderer()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource", "test", MetadataV2ResourceType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("binding", "resource", "test", storageLayerId: 1)
            .AddService("service", "service", protocols: ["OGC-API-Maps"])
            .AddPublication("publication", "service", "resource", layerIndex: 1)
            .BuildProvider();
        var renderer = Substitute.For<IRasterMapRenderer>();
        var pending = new TaskCompletionSource<RasterResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken? observed = null;
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.Arg<CancellationToken>();
                return pending.Task.WaitAsync(observed.Value);
            });
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/ogc/maps/map";
        context.Response.Body = new MemoryStream();
        var handler = new OgcMapsRenderingHandler(graph, renderer, Substitute.For<IOgcStyleProjection>(),
            NullLogger<OgcMapsRenderingHandler>.Instance);
        var endpoint = typeof(OgcMapsEndpoints).GetMethod("GetDatasetMap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var middleware = new LimitsEnforcementMiddleware(async requestContext =>
        {
            await (Task<IResult>)endpoint.Invoke(null,
                [new OgcMapRequest { Bbox = "-158,20,-156,22", Width = 1, Height = 1 }, requestContext, handler, CancellationToken.None])!;
        }, NullLogger<LimitsEnforcementMiddleware>.Instance,
            Options.Create(new LimitsOptions { Connections = new ConnectionLimits { RequestTimeout = TimeSpan.FromMilliseconds(100) } }));
        var request = middleware.InvokeAsync(context);
        try
        {
            await request.WaitAsync(TimeSpan.FromSeconds(5));
            observed.Should().NotBeNull("the request must reach the cooperative renderer");
            observed!.Value.IsCancellationRequested.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status408RequestTimeout);
            context.RequestAborted.IsCancellationRequested.Should().BeFalse("the client stayed connected");
        }
        finally
        {
            pending.TrySetResult(new RasterResult { Data = [1], ContentType = "image/png", Width = 1, Height = 1 });
            await request.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
