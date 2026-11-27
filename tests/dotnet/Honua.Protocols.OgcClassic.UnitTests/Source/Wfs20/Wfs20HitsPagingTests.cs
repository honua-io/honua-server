// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20HitsPagingTests
{
    private const string FeatureTypeName = "honua:svc-alpha-layer-1";

    [UnitTheory]
    [InlineData(1L, 1)]
    [InlineData(5L, 1)]
    [InlineData(5L, 2)]
    public async Task HandleGetFeatureAsync_HitsWithMatches_IncludesFirstPageNextLinkAndOmitsPrevious(
        long totalMatched,
        int count)
    {
        var handler = CreateHandler(totalMatched, count);
        using var services = CreateServiceProvider();

        var countText = count.ToString(CultureInfo.InvariantCulture);
        var hitsXml = await ExecuteGetFeatureXmlAsync(handler, services, countText, startIndex: null, resultType: "hits");
        var root = hitsXml.Root!;

        root.Attribute("numberMatched")?.Value.Should().Be(totalMatched.ToString(CultureInfo.InvariantCulture));
        root.Attribute("numberReturned")?.Value.Should().Be("0");
        root.Attribute("previous").Should().BeNull();

        var nextHref = root.Attribute("next")?.Value;
        nextHref.Should().NotBeNullOrWhiteSpace();
        var nextUri = new Uri(nextHref!);
        var query = QueryHelpers.ParseQuery(nextUri.Query);
        query["STARTINDEX"].ToString().Should().Be("0");
        query["COUNT"].ToString().Should().Be(countText);
        query.ContainsKey("RESULTTYPE").Should().BeFalse();

        // Following the advertised `next` link fetches the first page of actual results.
        var firstPageXml = await ExecuteGetFeatureXmlAsync(
            handler,
            services,
            query["COUNT"].ToString(),
            query["STARTINDEX"].ToString(),
            resultType: null);
        firstPageXml.Root!.Attribute("numberReturned")?.Value.Should().Be(countText);
    }

    [UnitTest]
    public async Task HandleGetFeatureAsync_HitsWithZeroMatches_OmitsPagingLinks()
    {
        var handler = CreateHandler(totalMatched: 0, pageSize: 1);
        using var services = CreateServiceProvider();

        var hitsXml = await ExecuteGetFeatureXmlAsync(handler, services, count: "1", startIndex: null, resultType: "hits");
        var root = hitsXml.Root!;

        root.Attribute("numberMatched")?.Value.Should().Be("0");
        root.Attribute("numberReturned")?.Value.Should().Be("0");
        root.Attribute("next").Should().BeNull();
        root.Attribute("previous").Should().BeNull();
    }

    private static async Task<XDocument> ExecuteGetFeatureXmlAsync(
        Wfs20Handler handler,
        IServiceProvider services,
        string? count,
        string? startIndex,
        string? resultType)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 8080);
        using var body = new MemoryStream();
        context.Response.Body = body;

        var result = await handler.HandleGetFeatureAsync(
            context,
            typeNames: FeatureTypeName,
            outputFormat: null,
            count: count,
            startIndex: startIndex,
            sortBy: null,
            bbox: null,
            filter: null,
            resourceId: null,
            propertyName: null,
            srsName: null,
            resultType: resultType);
        await result.ExecuteAsync(context);

        var bodyText = Encoding.UTF8.GetString(body.ToArray());
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, bodyText);
        return XDocument.Parse(bodyText);
    }

    private static ServiceProvider CreateServiceProvider()
        => new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

    private static Wfs20Handler CreateHandler(long totalMatched, int pageSize)
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(totalMatched);

        var items = Enumerable.Range(1, (int)Math.Min(totalMatched, pageSize))
            .Select(id => Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty))
            .ToImmutableArray();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResult<Feature>
            {
                TotalCount = totalMatched,
                Items = items,
                HasMoreResults = totalMatched > items.Length
            });

        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("res-layer-1", "Layer1", accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("binding-layer-1", "res-layer-1", "test.layers.1", storageLayerId: 1)
            .AddService("svc-alpha", "alpha", protocols: [MetadataV2ServiceProtocols.Wfs20],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddPublication("svc-alpha-layer-1", "svc-alpha", "res-layer-1", layerIndex: 1,
                storageBindingId: "binding-layer-1", publicationType: MetadataV2LayerType.OgcCollection)
            .Build();

        var queryProcessor = Substitute.For<IQueryProcessor>();
        queryProcessor.OptimizeQuery(Arg.Any<UnifiedQuery>(), Arg.Any<MetadataV2Object>())
            .Returns(call => call.Arg<UnifiedQuery>());
        queryProcessor.ValidateQuery(Arg.Any<UnifiedQuery>(), Arg.Any<MetadataV2Object>())
            .Returns(QueryValidationResult.Success());
        queryProcessor.ToFeatureQuery(Arg.Any<UnifiedQuery>(), Arg.Any<MetadataV2Object>())
            .Returns(new FeatureQuery());

        var coordinateTransformService = Substitute.For<ICoordinateTransformService>();
        var queryServices = new Wfs20QueryServices(
            reader,
            Substitute.For<IGmlFeatureStore>(),
            new TestMetadataV2GraphProvider(graph),
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            queryProcessor,
            Options.Create(new Wfs20Options()),
            Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>());

        var editServices = new Wfs20EditServices(
            Substitute.For<IFeatureWriter>(),
            new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance),
            Substitute.For<IEditProcessor>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            new FeatureMutationEventService(
                Substitute.For<IFeatureChangeEventPublisher>(),
                outboxCapabilityProvider: Substitute.For<IOutboxCapabilityProvider>()),
            Options.Create(new LimitsOptions()));

        var spatialServices = new Wfs20SpatialServices(
            new OgcFeaturesGeometryServices(
                Substitute.For<IGeometryService>(),
                coordinateTransformService,
                Options.Create(new LimitsOptions())),
            coordinateTransformService,
            Substitute.For<ICrsRegistry>());

        return new Wfs20Handler(NullLogger<Wfs20Handler>.Instance, queryServices, editServices, spatialServices);
    }
}
