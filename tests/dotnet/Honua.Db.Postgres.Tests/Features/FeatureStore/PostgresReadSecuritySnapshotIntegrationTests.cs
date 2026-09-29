// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresReadSecuritySnapshotIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;
    private readonly IRowLevelSecurityFilterSource _rows = Substitute.For<IRowLevelSecurityFilterSource>();
    private readonly IFieldMaskSource _masks = Substitute.For<IFieldMaskSource>();

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresReadSecuritySnapshotIntegrationTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.points (id bigint PRIMARY KEY, tenant text, secret text);
            INSERT INTO {{_schema}}.points VALUES
                (1, 'a', 'one'), (2, 'a', 'two'), (3, 'b', 'three'), (4, 'b', 'four');
            """);
        _rows.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns((SqlFragment?)null);
        _masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<string>.Empty);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTheory]
    [InlineData("query")]
    [InlineData("page-unlimited")]
    [InlineData("page-max")]
    public async Task CountedRead_NoPolicies_ResolvesEachPolicyOnce(string operation)
    {
        var result = await ReadAsync(CreateReader(), operation);

        result.Count.Should().Be(4);
        result.Items.Select(item => item.Id).Should().Equal(1L, 2L, 3L, 4L);
        await _rows.Received(1).ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>());
        await _masks.Received(1).ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTheory]
    [InlineData("query")]
    [InlineData("page-unlimited")]
    [InlineData("page-max")]
    public async Task CountedRead_PolicyChangesDuringOperation_CountAndFeaturesUseInitialPolicy(string operation)
    {
        var resolutions = 0;
        _rows.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++resolutions == 1 ? null : new SqlFragment("\"tenant\" = @p0", ["a"]));

        var result = await ReadAsync(CreateReader(), operation);

        // With nested public read calls, the first empty policy was resolved again
        // for the count, while the feature query still used the original policy.
        result.Count.Should().Be(4);
        result.Items.Select(item => item.Id).Should().Equal(1L, 2L, 3L, 4L);
        resolutions.Should().Be(1);
    }

    [IntegrationTest]
    public async Task QueryAsync_NextPublicRead_ResolvesNewPolicyAndMasks()
    {
        var reader = CreateReader();
        var query = new FeatureQuery { Limit = 100 };
        var first = await reader.QueryAsync(1, query);
        first.TotalCount.Should().Be(4);
        first.Items.Should().OnlyContain(item => item.Attributes.ContainsKey("secret"));

        _rows.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(new SqlFragment("\"tenant\" = @p0", ["a"]));
        _masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("secret"));

        var second = await reader.QueryAsync(1, query);
        second.TotalCount.Should().Be(2);
        second.Items.Select(item => item.Id).Should().Equal(1L, 2L);
        second.Items.Should().OnlyContain(item => !item.Attributes.ContainsKey("secret"));
        query.EnforcedSqlFilter.Should().BeNull();
        query.EnforcedMaskedFields.Should().BeNull();
        await _rows.Received(2).ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>());
        await _masks.Received(2).ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    public async Task CountAsync_DirectCalls_ResolvePolicyOnEveryCall()
    {
        var reader = CreateReader();
        var query = new FeatureQuery();
        (await reader.CountAsync(1, query)).Should().Be(4);
        _rows.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(new SqlFragment("\"tenant\" = @p0", ["b"]));

        (await reader.CountAsync(1, query)).Should().Be(2);
        await _rows.Received(2).ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>());
        await _masks.Received(2).ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTheory]
    [InlineData("query")]
    [InlineData("page-unlimited")]
    [InlineData("page-max")]
    public async Task CountedRead_WithPolicies_PreservesRowFilterAndMasks(string operation)
    {
        _rows.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(new SqlFragment("\"tenant\" = @p0", ["a"]));
        _masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("secret"));

        var result = await ReadAsync(CreateReader(), operation);
        result.Count.Should().Be(2);
        result.Items.Select(item => item.Id).Should().Equal(1L, 2L);
        result.Items.Should().OnlyContain(item => !item.Attributes.ContainsKey("secret"));
    }

    private static async Task<(long? Count, ImmutableArray<Feature> Items)> ReadAsync(
        PostgresStorageMappedFeatureReader reader, string operation)
    {
        if (operation == "query")
        {
            var result = await reader.QueryAsync(1, new FeatureQuery { Limit = 100 });
            return (result.TotalCount, result.Items);
        }

        var page = await reader.QueryPageAsync(1,
            new FeatureQuery { Limit = operation == "page-max" ? int.MaxValue : null });
        return (page.TotalCount, page.Items);
    }

    private PostgresStorageMappedFeatureReader CreateReader()
    {
        var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "security-snapshot", Name = "points" },
            SchemaFields =
            [
                new() { Name = "id", Type = MetadataV2FieldType.BigInteger },
                new() { Name = "tenant", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String }
            ]
        };
        return new PostgresStorageMappedFeatureReader(provider,
            new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
            resource, new FeatureStorageMapping("points", SchemaName: _schema, PrimaryKeyColumn: "id", GeometryColumn: null),
            connection: null, connectionEncryptionService: null, rlsFilterSource: _rows, fieldMaskSource: _masks);
    }
}
