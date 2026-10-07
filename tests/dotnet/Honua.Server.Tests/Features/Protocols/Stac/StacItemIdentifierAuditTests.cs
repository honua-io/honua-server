// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Filtering;
using Honua.Protocols.Stac.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Stac;

public sealed class StacItemIdentifierAuditTests
{
    [Fact]
    public void SRV_OGC_005_NonUniqueConventionNamedFieldFallsBackToFeatureIdentifier()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-items", Name = "items" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.BigInteger,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer }
            ]
        };
        var feature = Feature.Create(
            42,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("id", 0));

        Assert.Equal("42", StacMappingService.ResolveItemId(feature, resource));
        Assert.Collection(
            StacItemIdWhereBuilder.GetCandidateFields(resource),
            field => Assert.Equal("objectid", field.Name));
    }

    [Fact]
    public void SRV_OGC_018_IdFilterUsesTheIdentifierSerializedByStac()
    {
        var resource = CreateResource();
        var expression = new BinaryExpression(
            new PropertyReference("id"),
            BinaryOperator.Equal,
            new Literal("scene-42", LiteralType.Text));

        var rewritten = Assert.IsType<BinaryExpression>(
            Cql2FilterProcessor.RewriteStacCoreQueryables(expression, resource, "items"));

        Assert.Equal("stac_id", Assert.IsType<PropertyReference>(rewritten.Left).PropertyName);
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("not-a-number", false)]
    public async Task BoundLookup_UnannotatedSchema_UsesObjectIdsWithoutInventingAField(string itemId, bool matches)
    {
        var resource = CreateResource() with
        {
            SchemaFields = [new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.BigInteger }]
        };
        var feature = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty.Add("id", 0));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [feature]));

        var result = await StacBoundItemQueryExecutor.QueryAsync(
            reader, 7, resource, new FeatureQuery { Where = "name = 'keep'" }, [itemId], null, CancellationToken.None);

        Assert.Empty(StacItemIdWhereBuilder.GetCandidateFields(resource));
        if (matches)
        {
            Assert.Equal(feature, Assert.Single(result));
            await reader.Received(1).QueryAsync(7, Arg.Is<FeatureQuery>(query =>
                query.Where == "name = 'keep'" && query.ObjectIds.HasValue &&
                query.ObjectIds.Value.SequenceEqual(new long[] { 42 })), Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.Empty(result);
            await reader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
        }
    }

    private static MetadataV2Resource CreateResource() => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "res-items", Name = "items" },
        Type = MetadataV2ResourceType.FeatureDataset,
        SchemaFields =
        [
            new MetadataV2Field
            {
                Name = "stac_id",
                Type = MetadataV2FieldType.String,
                SemanticRoles = ["id.primary"]
            },
            new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer }
        ]
    };
}

[Collection("Database")]
[Protocol(TestProtocols.Stac)]
public sealed class StacItemIdentifierEndpointAuditTests : IAsyncLifetime
{
    private WebAppFixture? _fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _fixture?.DisposeAsync() ?? Task.CompletedTask;

    [IntegrationTheory]
    [InlineData(false, "stac_id", "scene-42")]
    [InlineData(true, "stac_id", "scene-42")]
    [InlineData(false, "catalog_key", "scene-42")]
    [InlineData(true, "catalog_key", "scene-42")]
    [InlineData(true, null, "42")]
    [Endpoint("GET /stac/collections/{collectionId}/items/{itemId}")]
    [Endpoint("GET /stac/search")]
    [Operation(Operations.StacSearch)]
    public async Task ItemAndIdsSearch_UseTheIdentifierEmittedByMapping(
        bool storageBound, string? primaryField, string itemId)
    {
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("id", 0);
        if (primaryField is not null)
        {
            attributes = attributes.Add(primaryField, itemId);
        }

        var feature = Feature.Create(42, null, attributes);
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<FeatureQuery>(1);
                if (primaryField is null)
                {
                    Assert.True(string.IsNullOrEmpty(query.Where));
                    Assert.Equal([42L], query.ObjectIds!.Value);
                }

                return QueryResult<Feature>.Create(1, [feature], false);
            });
        reader.QueryObjectIdsAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns([42L]);
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.ProviderName.Returns("postgis");
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadOnlyAnalytical);
        provider.Reader.Returns(reader);
        ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(Arg.Any<FeatureProviderBinding>()).Returns(reader);
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IFeatureDataProvider>();
            services.AddSingleton(provider);
            services.RemoveAll<IFeatureReader>();
            services.AddSingleton(reader);
        });
        _fixture = fixture;
        await fixture.InitializeAsync();
        var snapshot = fixture.GetCurrentV2GraphSnapshot();
        var publication = snapshot.Graph.Publications.First(p => p.LayerIndex == 0);
        var binding = snapshot.Graph.StorageBindings.First(b => b.ResourceId == publication.ResourceId && b.StorageLayerId.HasValue);
        var resource = snapshot.Index.ResourcesById[publication.ResourceId] with
        {
            SchemaFields = primaryField is null
                ? [new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.Integer }]
                : [new MetadataV2Field { Name = primaryField, Type = MetadataV2FieldType.String, SemanticRoles = ["id.primary"] }]
        };
        fixture.GetService<TestMetadataV2GraphProvider>().SetGraph(snapshot.Graph with
        {
            Resources = snapshot.Graph.Resources.Select(r => r.Metadata.Id == resource.Metadata.Id ? resource : r).ToArray(),
            Publications = snapshot.Graph.Publications.Select(p => p.LayerIndex == 0
                ? p with { StorageBindingId = storageBound ? binding.Metadata.Id : null } : p).ToArray(),
            Revision = snapshot.Graph.Revision + 1
        }, schema: fixture.CurrentSchema);

        var mapped = StacMappingService.MapFeatureToItem(feature, resource, publication, 0, "http://localhost");
        Assert.Equal(itemId, mapped.Id);
        var self = mapped.Links!.Value.Single(link => link.Rel == "self");
        var response = await fixture.Client.GetAsync(new Uri(self.Href).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var item = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(itemId, item.RootElement.GetProperty("id").GetString());

        // The provider-neutral executor is also shared by bound ids searches.
        if (storageBound)
        {
            var search = await fixture.Client.GetAsync($"/stac/search?collections=0&ids={itemId}");
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            using var results = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
            Assert.Equal(itemId, results.RootElement.GetProperty("features")[0].GetProperty("id").GetString());
        }
    }
}
