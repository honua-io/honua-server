// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TestProtocols = Honua.TestKit.Constants.ProtocolNames;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Protocol(TestProtocols.ODataV4)]
public sealed class ODataSourcePublicationTenantTests
{
    [UnitTest]
    public async Task HandleApplyAsync_HiddenPrimary_DoesNotRebindAuthorizedVisibleLayer()
    {
        var graph = CreateGraph();
        var provider = new TestMetadataV2GraphProvider(graph);
        using var services = CreateServices(provider, "tenant-a");
        var stream = Substitute.For<IStreamingFeatureStore>();
        stream.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(EmptyFeatures());
        var (search, query) = CreateSearch(provider, Substitute.For<IRelationshipStore>(), stream);
        var validation = new ODataValidationService(new CommonQueryValidator(Options.Create(new LimitsOptions())),
            Options.Create(new ODataOptions()));
        var handler = new ODataAdvancedQueryHandler(new ODataQuerySearchService(query, search), validation,
            NullLogger<ODataAdvancedQueryHandler>.Instance);
        var context = Context(services);

        var result = await handler.HandleApplyAsync(context, 7, "aggregate($count as n)");

        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        stream.Received(1).StreamFeaturesAsync(1, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        stream.DidNotReceive().StreamFeaturesAsync(2, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData(null, false)]
    [InlineData("tenant-a", false)]
    [InlineData("tenant-b", true)]
    public async Task ProcessExpandAsync_PublicationScopedChild_DoesNotReadForeignRows(string? tenantId, bool allowed)
    {
        var graph = CreateGraph();
        var child = graph.Resources.Single(resource => resource.Metadata.Id == "private");
        var parent = graph.Resources.Single(resource => resource.Metadata.Id == "public") with
        {
            Relationships = [new MetadataV2Relationship
            {
                Id = "children", Name = "Children", RelatedResourceId = child.Metadata.Id,
                OriginField = "join_id", DestinationField = "join_id"
            }]
        };
        var provider = new TestMetadataV2GraphProvider(graph with { Resources = [parent, child] });
        using var services = CreateServices(provider, tenantId);
        var relationships = Substitute.For<IRelationshipStore>();
        relationships.QueryRelatedAsync(1, Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1,
                [Feature.Create(11, null, new Dictionary<string, object?> { ["join_id"] = 101L, ["secret"] = "private" }.ToImmutableDictionary())]));
        var (search, _) = CreateSearch(provider, relationships, Substitute.For<IStreamingFeatureStore>());

        var result = await search.ProcessExpandAsync("Children", parent, 1, [101], Context(services));

        if (allowed)
        {
            result[101]["Children"].Should().ContainSingle();
            await relationships.Received(1).QueryRelatedAsync(1, Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result[101]["Children"].Should().BeEmpty();
            await relationships.DidNotReceive().QueryRelatedAsync(Arg.Any<int>(), Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>());
        }
    }

    private static (ODataSearchService Search, ODataQueryService Query) CreateSearch(
        IMetadataV2GraphProvider provider, IRelationshipStore relationships, IStreamingFeatureStore stream)
    {
        var reader = Substitute.For<IFeatureReader>();
        var query = new ODataQueryService(Substitute.For<IFilterExpressionService>(),
            Substitute.For<IQueryParameterAdapter<ODataQueryParameters>>(), Substitute.For<IQueryProcessor>());
        var search = new ODataSearchService(new ODataSearchDependencies(reader, relationships, stream,
            Substitute.For<IGeometryService>(), Substitute.For<ICrsRegistry>(), provider,
            new ODataFeatureProviderResolver(reader, Substitute.For<IFeatureWriter>(), null),
            Options.Create(new ODataOptions())), query);
        return (search, query);
    }

    private static MetadataV2Graph CreateGraph()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("public", "public", accessPolicy: new() { AllowAnonymous = true })
            .AddResource("private", "private", accessPolicy: new() { AllowAnonymous = true })
            .AddStorageBinding("public-binding", "public", "public", storageLayerId: 1)
            .AddStorageBinding("private-binding", "private", "private", storageLayerId: 2)
            .AddService("public-service", "public-service", protocols: ["OData"], accessPolicy: new() { AllowAnonymous = true })
            .AddService("private-service", "private-service", protocols: ["OData"], accessPolicy: new() { AllowAnonymous = true })
            .AddPublication("public-pub", "public-service", "public", layerIndex: 7, storageBindingId: "public-binding")
            .AddPublication("private-pub", "private-service", "private", layerIndex: 7, storageBindingId: "private-binding", isPrimary: true)
            .Build();
        return graph with
        {
            Publications = graph.Publications.Select(publication => publication.Metadata.Id == "private-pub"
                ? publication with { Metadata = publication.Metadata with { Tenant = "tenant-b" } }
                : publication).ToArray()
        };
    }

    private static ServiceProvider CreateServices(IMetadataV2GraphProvider graph, string? tenantId)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        return new ServiceCollection().AddLogging().AddValidationServices().AddSingleton(graph)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(tenant).BuildServiceProvider();
    }

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("honua.example");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async IAsyncEnumerable<Feature> EmptyFeatures()
    {
        await Task.CompletedTask;
        yield break;
    }
}
