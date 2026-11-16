// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

[Trait("Tier", "Fast")]
[Trait("Category", "Unit")]
public sealed class VectorTileFieldVisibilityTests
{
    [Fact]
    public async Task ExecuteAsync_HiddenField_MasksItTogetherWithCallerMasks()
    {
        var served = await ServeAsync(TileTestLayer.Create(hiddenField: "secret"), callerMasks: ["salary"]);

        served.StatusCode.Should().Be(StatusCodes.Status200OK);
        served.Query!.Value.EnforcedMaskedFields!.Value.Should().BeEquivalentTo("secret", "salary");
    }

    [Fact]
    public async Task ExecuteAsync_HiddenField_KeepsMasksTheCallerAlreadyEnforced()
    {
        var query = new FeatureQuery { EnforcedMaskedFields = ["owner"] };

        var served = await ServeAsync(TileTestLayer.Create(hiddenField: "secret"), callerMasks: ["salary"], query);

        served.Query!.Value.EnforcedMaskedFields!.Value.Should().BeEquivalentTo("secret", "owner");
    }

    [Fact]
    public async Task ExecuteAsync_NoHiddenField_LeavesMaskResolutionToTheProvider()
    {
        var served = await ServeAsync(TileTestLayer.Create(hiddenField: null), callerMasks: ["salary"]);

        served.StatusCode.Should().Be(StatusCodes.Status200OK);
        served.Query!.Value.EnforcedMaskedFields.Should().BeNull();
    }

    [Theory]
    [InlineData("secret = 'a'")]
    [InlineData("SECRET IS NOT NULL")]
    public async Task ExecuteAsync_FilterOnHiddenField_IsRefusedBeforeTheProviderRuns(string where)
    {
        var served = await ServeAsync(TileTestLayer.Create(hiddenField: "secret"), callerMasks: [], new FeatureQuery { Where = where });

        served.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        served.Query.Should().BeNull("the provider must not evaluate a filter on a hidden field");
        served.Body.Should().Contain("not available").And.NotContain("secret");
    }

    [Fact]
    public async Task ExecuteAsync_FilterOnCallerMaskedField_IsRefused()
    {
        var query = new FeatureQuery { Where = "salary > 10" };

        var served = await ServeAsync(TileTestLayer.Create(hiddenField: "secret"), callerMasks: ["salary"], query);

        served.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        served.Query.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TimeFilterOnHiddenField_IsRefused()
    {
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "secret",
                PropertyType = TemporalPropertyType.DateTime,
                Start = DateTimeOffset.UnixEpoch
            }
        };

        var served = await ServeAsync(TileTestLayer.Create(hiddenField: "secret"), callerMasks: [], query);

        served.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        served.Query.Should().BeNull();
    }

    private static async Task<Served> ServeAsync(MetadataV2Object resource, string[] callerMasks, FeatureQuery? query = null)
    {
        var maskSource = Substitute.For<IFieldMaskSource>();
        maskSource.ResolveAsync(resource, Arg.Any<CancellationToken>()).Returns([.. callerMasks]);
        using var services = new ServiceCollection().AddLogging().AddSingleton(maskSource).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/ogc/tiles/collections/test/tiles/WebMercatorQuad/0/0/0";
        context.Response.Body = new MemoryStream();

        FeatureQuery? received = null;
        var provider = Substitute.For<ITileProvider>();
        provider.GetMvtTileAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Do<FeatureQuery?>(value => received = value), Arg.Any<TileOptions>(), Arg.Any<TileLimits>(),
            Arg.Any<GridGeometry?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>([1, 2, 3]));

        var tileQuery = await VectorTileQuery.ForLayerAsync(context, resource, query ?? new FeatureQuery());
        var result = await VectorTileExecution.ExecuteAsync(context, provider, 1, 0, 0, 0,
            tileQuery, new TileOptions(), new TileLimits(), CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return new Served(context.Response.StatusCode, received, await reader.ReadToEndAsync());
    }

    private sealed record Served(int StatusCode, FeatureQuery? Query, string Body);
}
