// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Queries.Filters;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class SourceBackedRelationshipStoreIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(SourceBackedRelationshipStoreIntegrationTests));
        await fixture.ExecuteAsync($"""
            CREATE TABLE {_schema}.parents (objectid bigint PRIMARY KEY, join_id integer, details text);
            CREATE TABLE {_schema}.children (objectid bigint PRIMARY KEY, join_id integer, details text);
            INSERT INTO {_schema}.parents VALUES (901,2055,'first'), (902,2055,'shared'), (903,2068,'last'), (904,NULL,'missing');
            INSERT INTO {_schema}.children VALUES (81,2055,'low'), (82,2068,'loud'), (83,2068,'low'), (84,9999,'orphan');
            """);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [Fact]
    public async Task QueryRelatedAsync_ImportedTables_ReturnsEveryMatchingChildWithTrueParentIds()
    {
        var store = CreateStore();
        var result = await store.QueryRelatedAsync(13, RelatedQuery.ForObjects([901, 902, 903, 904], 14, "join_id", "join_id"));

        result.Items.Select(row => row.Id).Should().BeEquivalentTo([81L, 82L, 83L]);
        result.Items.Single(row => row.Id == 81).Attributes[RelatedQuery.OriginObjectIdsAttribute]
            .Should().BeEquivalentTo(new long[] { 901, 902 });
        result.Items.Where(row => row.Id != 81).Should().OnlyContain(row =>
            ((long[])row.Attributes[RelatedQuery.OriginObjectIdsAttribute]!).SequenceEqual(new long[] { 903 }));
        result.Items.Should().OnlyContain(row => row.Geometry == null);
    }

    [Fact]
    public async Task QueryRelatedAsync_FilterAndNarrowProjection_PreservesPredicateAndGrouping()
    {
        var result = await CreateStore().QueryRelatedAsync(13,
            RelatedQuery.ForObjects([901, 903], 14, "join_id", "join_id") with
            {
                SqlFilter = new SqlFragment("attributes->>'details' = @p0", ["low"]),
                OutFields = ["details"]
            });

        result.Items.Select(row => row.Id).Should().BeEquivalentTo([81L, 83L]);
        result.Items.Should().OnlyContain(row => !row.Attributes.ContainsKey("join_id") &&
            row.Attributes.ContainsKey(RelatedQuery.OriginObjectIdsAttribute));
    }

    [Fact]
    public async Task QueryRelatedAsync_ObjectIdKey_UsesTheMappedPrimaryKey()
    {
        await fixture.ExecuteAsync($"INSERT INTO {_schema}.children VALUES (85,901,'object-id join')");
        var result = await CreateStore().QueryRelatedAsync(13,
            RelatedQuery.ForObjects([901], 14, "objectid", "join_id"));
        result.Items.Should().ContainSingle().Which.Id.Should().Be(85);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueryRelatedAsync_BooleanKey_MatchesPostgresText(bool key)
    {
        await fixture.ExecuteAsync($"""
            ALTER TABLE {_schema}.parents ALTER COLUMN join_id TYPE boolean USING join_id = 2055;
            ALTER TABLE {_schema}.children ALTER COLUMN join_id TYPE boolean USING join_id = 2055;
            """);
        var result = await CreateStore(MetadataV2FieldType.Boolean).QueryRelatedAsync(13,
            RelatedQuery.ForObjects([key ? 901 : 903], 14, "join_id", "join_id"));

        result.Items.Select(row => row.Id).Should().BeEquivalentTo(key ? new long[] { 81 } : [82L, 83L, 84L]);
        result.Items.Should().OnlyContain(row =>
            ((long[])row.Attributes[RelatedQuery.OriginObjectIdsAttribute]!).SequenceEqual(new long[] { key ? 901 : 903 }));
    }

    [Fact]
    public async Task QueryRelatedAsync_CanonicalWhere_PreservesCallerPredicate()
    {
        var result = await CreateStore().QueryRelatedAsync(13,
            RelatedQuery.ForObjects([901, 903], 14, "join_id", "join_id") with { Where = "details = 'low'" });

        result.Items.Select(row => row.Id).Should().BeEquivalentTo([81L, 83L]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryRelatedAsync_MaskedJoinKeys_MatchesWithoutExposingMaskedAttributes(bool explicitProjection)
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("join_id", "details"));
        var result = await CreateStore(fieldMasks: masks).QueryRelatedAsync(13,
            RelatedQuery.ForObjects([901, 902, 903], 14, "join_id", "join_id") with
            {
                OutFields = explicitProjection ? ["objectid", "join_id", "details"] : null
            });

        result.Items.Select(row => row.Id).Should().BeEquivalentTo([81L, 82L, 83L]);
        result.Items.Single(row => row.Id == 81).Attributes[RelatedQuery.OriginObjectIdsAttribute]
            .Should().BeEquivalentTo(new long[] { 901, 902 });
        result.Items.Should().OnlyContain(row => !row.Attributes.ContainsKey("join_id") &&
            !row.Attributes.ContainsKey("details"));
    }

    [Fact]
    public async Task QueryRelatedAsync_CallerPredicateOnMaskedJoinKey_IsRejected()
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("join_id"));
        var action = () => CreateStore(fieldMasks: masks).QueryRelatedAsync(13,
            RelatedQuery.ForObjects([901], 14, "join_id", "join_id") with { Where = "join_id = 2055" });

        await action.Should().ThrowAsync<ArgumentException>();
    }

    private SourceBackedRelationshipStore CreateStore(MetadataV2FieldType keyType = MetadataV2FieldType.Integer,
        IFieldMaskSource? fieldMasks = null)
    {
        var service = new MetadataV2Service { Metadata = new() { Id = "service" } };
        var resources = new[] { Resource("parents", 13, keyType), Resource("children", 14, keyType) };
        var bindings = resources.Select((resource, index) => new MetadataV2StorageBinding
        {
            Metadata = new() { Id = resource.StorageBindingIds[0] },
            ResourceId = resource.Metadata.Id,
            StorageType = MetadataV2StorageType.RelationalTable,
            StorageLayerId = index + 13,
            Locator = _schema + "." + resource.Metadata.Id,
            Options = new Dictionary<string, JsonElement> { ["sourceBacked"] = JsonSerializer.SerializeToElement(true) }
        }).ToArray();
        var publications = bindings.Select(binding => new MetadataV2Publication
        {
            Metadata = new() { Id = "pub-" + binding.ResourceId },
            ResourceId = binding.ResourceId,
            ServiceId = service.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            Identifier = new() { Value = binding.StorageLayerId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), IsNumeric = true }
        }).ToArray();
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Resources = resources,
            StorageBindings = bindings,
            Services = [service],
            Publications = publications
        }, "test", DateTimeOffset.UtcNow);
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        graph.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var connections = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        connections.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync(call.Arg<CancellationToken>());
            return (DbConnection)connection;
        });
        var pool = new DefaultObjectPoolProvider().Create(new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(Arg.Any<FeatureProviderBinding>()).Returns(call =>
        {
            var binding = call.Arg<FeatureProviderBinding>();
            return new PostgresStorageMappedFeatureReader(connections, pool, binding.Resource, binding.StorageMapping, null, null,
                fieldMaskSource: fieldMasks);
        });
        var router = new FeatureProviderQueryRouter(Substitute.For<ISecureConnectionRegistry>(), new FeatureDataProviderRegistry([provider]));
        return new SourceBackedRelationshipStore(Substitute.For<IRelationshipStore>(), graph, router,
            new FilterExpressionService(new FilterExpressionTranslator(new PostgresSqlFilterTranslator(useJsonAttributes: true))),
            fieldMasks);
    }

    private static MetadataV2Resource Resource(string name, int layerId, MetadataV2FieldType keyType) => new()
    {
        Metadata = new() { Id = name },
        Type = MetadataV2ResourceType.Table,
        StorageBindingIds = ["binding-" + layerId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        SchemaFields =
        [
            new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, SemanticRoles = ["id.primary"] },
            new() { Name = "join_id", Type = keyType },
            new() { Name = "details", Type = MetadataV2FieldType.String }
        ]
    };
}
