// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Protocols.Ogc.Api.Edr;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Protocols.OgcApi.UnitTests.Edr;

public sealed class EdrStorageCollectionTests
{
    private const int SharedStorageId = 42;
    private const int DecoyStorageId = 7;
    private const long RasterId = 9001;

    [UnitTest]
    public async Task CollectionsPositionAndCube_BindAdvertisedStorageLayer_AndDoNotRepeatIt()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        var raster = new RasterInfo
        {
            Id = RasterId,
            LayerId = SharedStorageId,
            Name = "shared",
            Width = 8,
            Height = 8,
            BandCount = 1,
            PixelType = "F32",
        };
        rasterStore.GetPrimaryRasterInfoAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(null));
        rasterStore.GetPrimaryRasterInfoAsync(SharedStorageId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));
        rasterStore.IdentifyAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<int?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new PixelValueResult
            {
                X = call.ArgAt<double>(2),
                Y = call.ArgAt<double>(3),
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 3d },
            }));

        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc", "coverages", protocols: [ServiceProtocols.OgcApiCoverages])
            .AddResource("west", "west-dem", MetadataV2ObjectType.RasterDataset, accessPolicy: Anonymous())
            .AddResource("east", "east-dem", MetadataV2ObjectType.RasterDataset, accessPolicy: Anonymous())
            .AddResource("decoy", "decoy-dem", MetadataV2ObjectType.RasterDataset, accessPolicy: Anonymous())
            .AddStorageBinding("west-binding", "west", "west", storageLayerId: SharedStorageId)
            .AddStorageBinding("east-binding", "east", "east", storageLayerId: SharedStorageId)
            .AddStorageBinding("decoy-binding", "decoy", "decoy", storageLayerId: DecoyStorageId)
            .AddPublication(
                "pub-west",
                "svc",
                "west",
                storageBindingId: "west-binding",
                serviceLocalId: "west-dem",
                isPrimary: true,
                publicationType: MetadataV2LayerType.OgcCollection)
            .AddPublication(
                "pub-east",
                "svc",
                "east",
                storageBindingId: "east-binding",
                serviceLocalId: "east-dem",
                publicationType: MetadataV2LayerType.OgcCollection)
            .AddPublication(
                "pub-decoy",
                "svc",
                "decoy",
                storageBindingId: "decoy-binding",
                serviceLocalId: SharedStorageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                publicationType: MetadataV2LayerType.OgcCollection)
            .BuildProvider();
        var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(graph)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddLogging()
            .BuildServiceProvider();
        var handler = new EdrHandler(new EdrDependencies(
            graph,
            rasterStore,
            Substitute.For<ICoordinateTransformService>()));

        using var listed = await ExecuteAsync(
            services,
            await handler.GetCollectionsAsync(Context(services, "/edr/collections", ""), CancellationToken.None));
        var ids = listed.RootElement.GetProperty("collections").EnumerateArray()
            .Select(collection => collection.GetProperty("id").GetString())
            .ToArray();
        ids.Should().Equal(SharedStorageId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var collection = await ExecuteAsync(
            services,
            await handler.GetCollectionAsync(
                Context(services, "/edr/collections/42", ""),
                SharedStorageId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None));
        collection.RootElement.GetProperty("id").GetString().Should().Be("42");
        collection.RootElement.GetProperty("title").GetString().Should().Be("west-dem");

        using var position = await ExecuteAsync(
            services,
            await handler.GetPositionAsync(
                Context(services, "/edr/collections/42/position", "?coords=POINT(1%202)"),
                "42",
                CancellationToken.None));
        position.RootElement.GetProperty("type").GetString().Should().Be("Coverage");

        using var cube = await ExecuteAsync(
            services,
            await handler.GetCubeAsync(
                Context(services, "/edr/collections/42/cube", "?bbox=0,0,2,2&resolution-x=1"),
                "42",
                CancellationToken.None));
        cube.RootElement.GetProperty("domain").GetProperty("domainType").GetString().Should().Be("Grid");

        await rasterStore.Received().IdentifyAsync(
            SharedStorageId,
            RasterId,
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<int?>(),
            Arg.Any<RasterIdentifyRendering?>(),
            Arg.Any<CancellationToken>());
        await rasterStore.DidNotReceive().IdentifyAsync(
            DecoyStorageId,
            Arg.Any<long>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<int?>(),
            Arg.Any<RasterIdentifyRendering?>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task EdrHandler_EnforcesStrictQueryParameterAcceptWktBboxAndResolutionValidation()
    {
        var (services, handler) = BuildTestHandler();

        // 1. Unknown query parameters return 400 across all endpoints.
        (await StatusAsync(services, EdrHandler.GetLandingPage(Context(services, "/edr", "?unknown=1"))))
            .Should().Be(StatusCodes.Status400BadRequest);
        (await StatusAsync(services, EdrHandler.GetConformance(Context(services, "/edr/conformance", "?unknown=1"))))
            .Should().Be(StatusCodes.Status400BadRequest);
        (await StatusAsync(services, await handler.GetCollectionsAsync(Context(services, "/edr/collections", "?unknown=1"), CancellationToken.None)))
            .Should().Be(StatusCodes.Status400BadRequest);
        (await StatusAsync(services, await handler.GetCollectionAsync(Context(services, "/edr/collections/42", "?unknown=1"), "42", CancellationToken.None)))
            .Should().Be(StatusCodes.Status400BadRequest);
        (await StatusAsync(services, await handler.GetPositionAsync(Context(services, "/edr/collections/42/position", "?coords=POINT(1%202)&unknown=1"), "42", CancellationToken.None)))
            .Should().Be(StatusCodes.Status400BadRequest);
        (await StatusAsync(services, await handler.GetCubeAsync(Context(services, "/edr/collections/42/cube", "?bbox=0,0,2,2&unknown=1"), "42", CancellationToken.None)))
            .Should().Be(StatusCodes.Status400BadRequest);

        // 2. Unacceptable Accept headers return 406 across metadata and coverage endpoints.
        (await StatusAsync(services, EdrHandler.GetLandingPage(Context(services, "/edr", "", accept: "text/html"))))
            .Should().Be(StatusCodes.Status406NotAcceptable);
        (await StatusAsync(services, EdrHandler.GetConformance(Context(services, "/edr/conformance", "", accept: "text/html"))))
            .Should().Be(StatusCodes.Status406NotAcceptable);
        (await StatusAsync(services, await handler.GetPositionAsync(Context(services, "/edr/collections/42/position", "?coords=POINT(1%202)", accept: "text/xml"), "42", CancellationToken.None)))
            .Should().Be(StatusCodes.Status406NotAcceptable);
        (await StatusAsync(services, await handler.GetCubeAsync(Context(services, "/edr/collections/42/cube", "?bbox=0,0,2,2", accept: "text/xml"), "42", CancellationToken.None)))
            .Should().Be(StatusCodes.Status406NotAcceptable);

        // 3. f=application/prs.coverage+json and f=application/prs.coveragejson+json both succeed.
        foreach (var f in new[] { "application/prs.coverage+json", "application/prs.coveragejson+json" })
        {
            (await StatusAsync(services, await handler.GetPositionAsync(
                    Context(services, "/edr/collections/42/position", $"?coords=POINT(1%202)&f={Uri.EscapeDataString(f)}"),
                    "42",
                    CancellationToken.None)))
                .Should().Be(StatusCodes.Status200OK);
        }

        // 4. Malformed WKT coords are rejected with 400.
        foreach (var badCoords in new[]
                 {
                     "POINTFOO(1 2)",
                     "POINT(1, 2)",
                     "POINT(1 2)extra",
                     "POINT(NaN 2)",
                     "MULTIPOINTFOO((1 2))",
                     "MULTIPOINT((1, 2))"
                 })
        {
            (await StatusAsync(services, await handler.GetPositionAsync(
                    Context(services, "/edr/collections/42/position", $"?coords={Uri.EscapeDataString(badCoords)}"),
                    "42",
                    CancellationToken.None)))
                .Should().Be(StatusCodes.Status400BadRequest, badCoords);
        }

        // 5. 6-element finite bbox succeeds; non-finite bbox and invalid resolution-x return 400.
        (await StatusAsync(services, await handler.GetCubeAsync(
                Context(services, "/edr/collections/42/cube", "?bbox=0,0,0,2,2,10&resolution-x=1"),
                "42",
                CancellationToken.None)))
            .Should().Be(StatusCodes.Status200OK);

        foreach (var badCubeQuery in new[]
                 {
                     "?bbox=0,0,Infinity,2",
                     "?bbox=0,0,10,2,2,0",
                     "?bbox=0,0,2,2&resolution-x=-5",
                     "?bbox=0,0,2,2&resolution-x=0",
                     "?bbox=0,0,2,2&resolution-x=abc"
                 })
        {
            (await StatusAsync(services, await handler.GetCubeAsync(
                    Context(services, "/edr/collections/42/cube", badCubeQuery),
                    "42",
                    CancellationToken.None)))
                .Should().Be(StatusCodes.Status400BadRequest, badCubeQuery);
        }
    }

    private static (IServiceProvider Services, EdrHandler Handler) BuildTestHandler()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        var raster = new RasterInfo
        {
            Id = RasterId,
            LayerId = SharedStorageId,
            Name = "shared",
            Width = 8,
            Height = 8,
            BandCount = 1,
            PixelType = "F32",
        };
        rasterStore.GetPrimaryRasterInfoAsync(SharedStorageId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RasterInfo?>(raster));
        rasterStore.IdentifyAsync(
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<int?>(),
                Arg.Any<RasterIdentifyRendering?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new PixelValueResult
            {
                X = call.ArgAt<double>(2),
                Y = call.ArgAt<double>(3),
                HasData = true,
                BandValues = new Dictionary<int, object?> { [1] = 3d },
            }));

        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc", "coverages", protocols: [ServiceProtocols.OgcApiCoverages])
            .AddResource("west", "west-dem", MetadataV2ObjectType.RasterDataset, accessPolicy: Anonymous())
            .AddStorageBinding("west-binding", "west", "west", storageLayerId: SharedStorageId)
            .AddPublication(
                "pub-west",
                "svc",
                "west",
                storageBindingId: "west-binding",
                serviceLocalId: "west-dem",
                isPrimary: true,
                publicationType: MetadataV2LayerType.OgcCollection)
            .BuildProvider();
        var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(graph)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddLogging()
            .BuildServiceProvider();
        var handler = new EdrHandler(new EdrDependencies(
            graph,
            rasterStore,
            Substitute.For<ICoordinateTransformService>()));
        return (services, handler);
    }

    private static AccessPolicy Anonymous() => new() { AllowAnonymous = true };

    private static DefaultHttpContext Context(IServiceProvider services, string path, string query, string? accept = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        if (query.Length > 0)
        {
            context.Request.QueryString = new QueryString(query);
        }

        if (!string.IsNullOrEmpty(accept))
        {
            context.Request.Headers.Accept = accept;
        }

        context.Response.Body = new MemoryStream();
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        return context;
    }

    private static async Task<int> StatusAsync(IServiceProvider services, IResult result)
    {
        var context = Context(services, "/", "");
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    private static async Task<JsonDocument> ExecuteAsync(IServiceProvider services, IResult result)
    {
        var context = Context(services, "/", "");
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }
}
