// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters.OData;
using Honua.Protocols.OData.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.OData;

public sealed class ODataCloudNativeRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextLink_Bbox_PreservesWindow(bool useSkipToken)
    {
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Public:BaseUrl"] = "https://example.test" }).Build()).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        context.Request.Path = "/odata/Layers(4)/Features";
        context.Request.QueryString = new QueryString("?bbox=-123,37,-121,39");

        var link = ODataUtilityService.GenerateNextLink(
            context.Request, 2, 2, null, null, null, null, useSkipToken: useSkipToken);

        QueryHelpers.ParseQuery(new Uri(link).Query)["bbox"].ToString()
            .Should().Be("-123,37,-121,39");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?bbox=-125,35,-120,40")]
    public void SkipToken_BboxChangedOrRemoved_RejectsReuse(string nextQuery)
    {
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Public:BaseUrl"] = "https://example.test" }).Build()).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/odata/Layers(4)/Features";
        context.Request.QueryString = new QueryString("?bbox=-123,37,-121,39");
        var link = ODataUtilityService.GenerateNextLink(context.Request, 2, 2,
            null, null, null, null, useSkipToken: true);
        var token = QueryHelpers.ParseQuery(new Uri(link).Query)["$skiptoken"].ToString();
        var validation = new ODataValidationService(
            new Honua.Core.Features.Validation.CommonQueryValidator(
                Microsoft.Extensions.Options.Options.Create(new Honua.Core.Configuration.LimitsOptions())),
            Microsoft.Extensions.Options.Options.Create(new Honua.Protocols.OData.ODataOptions()));
        ODataRequestValidation.TryParsePaging(context, validation, "2", null, token, null,
            out _, out _).Should().BeTrue();
        context.Request.QueryString = new QueryString(nextQuery);

        ODataRequestValidation.TryParsePaging(context, validation, "2", null, token, null,
            out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("POINT(12 34)")]
    [InlineData("POINT(-13630000 4540000)")]
    public void GeometryLiteral_WithoutSrid_RejectsAmbiguousCoordinates(string wkt)
    {
        var parse = () => new ODataFilterParser().Parse($"geo.intersects(Geometry, geometry'{wkt}')");
        parse.Should().Throw<ODataFilterParseException>().WithMessage("*SRID*");
    }

    [Fact]
    public async Task Apply_Count_BoundsProviderQuery()
    {
        var reader = Substitute.For<IFeatureReader>();
        var stream = Substitute.For<IStreamingFeatureStore>();
        FeatureQuery? observed = null;
        stream.StreamFeaturesAsync(41, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => { observed = call.Arg<FeatureQuery>(); return Features(1); });
        var handler = new ODataAggregationHandler(reader, stream, null!);

        await handler.ProcessAggregationAsync(41, Resource(), "aggregate($count as n)", null,
            "https://example.test", CancellationToken.None);

        observed.Should().NotBeNull();
        observed!.Value.Limit.Should().Be(10001);
    }

    [Theory]
    [InlineData("aggregate($count as n)")]
    [InlineData("groupby((ObjectId))")]
    public async Task Apply_TooManyRows_RejectsInsteadOfReturningPartialAggregate(string apply)
    {
        var stream = Substitute.For<IStreamingFeatureStore>();
        stream.StreamFeaturesAsync(41, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Features(10001));
        var handler = new ODataAggregationHandler(Substitute.For<IFeatureReader>(), stream, null!);

        var act = () => handler.ProcessAggregationAsync(41, Resource(), apply, null,
            "https://example.test", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*10000*");
    }

    [Theory]
    [InlineData("aggregate($count as n)")]
    [InlineData("groupby((ObjectId))")]
    [InlineData("compute(ObjectId add 1 as n)")]
    public async Task Apply_NonStreamingProviderWithContinuation_RejectsIncompleteInput(string apply)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(41, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(3, [Feature.Create(1, null)], hasMoreResults: true));
        var handler = new ODataAggregationHandler(reader, null, null!, maxInputRows: 2);

        var act = () => handler.ProcessAggregationAsync(41, Resource(), apply, null,
            "https://example.test", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*maximum input row count of 2*");
        await reader.Received().QueryAsync(41, Arg.Is<FeatureQuery>(query => query.Limit == 3),
            Arg.Any<CancellationToken>());
    }

    private static MetadataV2Resource Resource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res", Name = "test" },
        Type = MetadataV2ResourceType.FeatureDataset
    };

    private static async IAsyncEnumerable<Feature> Features(int count)
    {
        await Task.CompletedTask;
        for (var i = 0; i < count; i++)
        {
            yield return Feature.Create(i, null);
        }
    }
}
