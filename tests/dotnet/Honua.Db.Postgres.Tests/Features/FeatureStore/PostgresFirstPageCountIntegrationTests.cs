// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
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
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresFirstPageCountIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string _schema = null!;
    private readonly IAdoNetDatabaseConnectionProvider _provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
    private readonly IRowLevelSecurityFilterSource _rows = Substitute.For<IRowLevelSecurityFilterSource>();
    private readonly IFieldMaskSource _masks = Substitute.For<IFieldMaskSource>();

    public async Task InitializeAsync()
    {
        _schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresFirstPageCountIntegrationTests));
        await fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.points (id bigint PRIMARY KEY, geom geometry(Point,4326), tenant text, secret text);
            INSERT INTO {{_schema}}.points VALUES
                (1, ST_SetSRID(ST_MakePoint(0,0),4326), 'a', 'one'),
                (2, ST_SetSRID(ST_MakePoint(1,1),4326), 'a', 'two'),
                (3, ST_SetSRID(ST_MakePoint(2,2),4326), 'b', 'three'),
                (4, ST_SetSRID(ST_MakePoint(3,3),4326), 'b', 'four');
            """);
        _provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
        _rows.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns((SqlFragment?)null);
        _masks.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<string>.Empty);
    }

    public Task DisposeAsync() => fixture.DropSchemaAsync(_schema);

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task QueryAsync_ShortFirstPage_ReturnsExactTotalWithOneDatabaseRead(int? offset)
    {
        using var observer = ObserveSelectCommands();
        var result = await CreateReader().QueryAsync(1, new FeatureQuery { Limit = 10, Offset = offset });

        result.TotalCount.Should().Be(4);
        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L, 3L, 4L);
        result.Items.Should().OnlyContain(feature => feature.Geometry != null);
        result.HasMoreResults.Should().BeFalse();
        await _provider.Received(1).OpenConnectionAsync(Arg.Any<CancellationToken>());
        observer.Count.Should().Be(1);
    }

    [IntegrationTest]
    public async Task QueryAsync_ShortSpatialPage_PreservesBoundaryAndSecurityWithOneDatabaseRead()
    {
        _rows.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(new SqlFragment("\"tenant\" = @p0", ["a"]));
        _masks.ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("secret"));
        var query = new FeatureQuery
        {
            Limit = 100,
            SpatialFilter = new SpatialFilter
            {
                Geometry = new WKTReader().Read("POLYGON((0 0,0 2,2 2,2 0,0 0))").AsBinary(),
                Srid = 4326,
                SpatialRelationship = SpatialRelationship.Intersects
            }
        };

        using var observer = ObserveSelectCommands();
        var result = await CreateReader().QueryAsync(1, query);

        result.TotalCount.Should().Be(2);
        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        result.Items.Should().OnlyContain(feature => !feature.Attributes.ContainsKey("secret"));
        result.HasMoreResults.Should().BeFalse();
        await _provider.Received(1).OpenConnectionAsync(Arg.Any<CancellationToken>());
        observer.Count.Should().Be(1);
        await _rows.Received(1).ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>());
        await _masks.Received(1).ResolveAsync(Arg.Any<MetadataV2Object>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTheory]
    [InlineData(2, 0, 2, true)]
    [InlineData(4, 0, 4, false)]
    [InlineData(10, 1, 3, false)]
    [InlineData(10, 20, 0, false)]
    [InlineData(0, 0, 0, true)]
    [InlineData(null, 0, 4, false)]
    public async Task QueryAsync_PageCannotProveTotal_RetainsExactCount(
        int? limit, int offset, int expectedItems, bool hasMore)
    {
        using var observer = ObserveSelectCommands();
        var result = await CreateReader().QueryAsync(1, new FeatureQuery { Limit = limit, Offset = offset });

        result.TotalCount.Should().Be(4);
        result.Items.Should().HaveCount(expectedItems);
        result.HasMoreResults.Should().Be(hasMore);
        await _provider.Received(1).OpenConnectionAsync(Arg.Any<CancellationToken>());
        observer.Count.Should().Be(2);
    }

    [IntegrationTest]
    public async Task QueryAsync_EmptyFirstPage_ReturnsZeroWithOneDatabaseRead()
    {
        using var observer = ObserveSelectCommands();
        var result = await CreateReader().QueryAsync(1, new FeatureQuery { Limit = 100, Where = "id = 999" });

        result.TotalCount.Should().Be(0);
        result.Items.Should().BeEmpty();
        result.HasMoreResults.Should().BeFalse();
        await _provider.Received(1).OpenConnectionAsync(Arg.Any<CancellationToken>());
        observer.Count.Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task QueryAsync_FullFirstPageWithConcurrentWrites_PreservesPageAndReconcilesTotal(int laterCount)
    {
        using var observer = ObserveSelectCommands(onFirstCommandStopped: () =>
        {
            // The page has been materialized. Commit another writer's changes
            // before the count command runs on the shared connection, without timing-dependent sleeps.
            fixture.ExecuteAsync($$"""
                DELETE FROM {{_schema}}.points;
                INSERT INTO {{_schema}}.points
                SELECT id, ST_SetSRID(ST_MakePoint(id,id),4326), 'a', 'replacement'
                FROM generate_series(1, {{laterCount}}) AS id;
                """).GetAwaiter().GetResult();
        });

        var result = await CreateReader().QueryAsync(1, new FeatureQuery { Limit = 2 });

        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        result.Items.Select(feature => feature.Attributes["secret"]).Should().Equal("one", "two");
        result.TotalCount.Should().Be(Math.Max(2, laterCount));
        result.HasMoreResults.Should().Be(laterCount > 2);
        await _provider.Received(1).OpenConnectionAsync(Arg.Any<CancellationToken>());
        observer.Count.Should().Be(2);
    }

    [IntegrationTest]
    public async Task QueryAsync_NearestNeighbors_RetainsNearestCountWithoutAnExtraRead()
    {
        using var observer = ObserveSelectCommands();
        var result = await CreateReader().QueryAsync(1, new FeatureQuery
        {
            Limit = 100,
            SpatialFilter = new SpatialFilter
            {
                Geometry = new WKTReader().Read("POINT(0 0)").AsBinary(),
                Srid = 4326,
                SpatialRelationship = SpatialRelationship.NearestNeighbor,
                NearestCount = 2
            }
        });

        result.TotalCount.Should().Be(2);
        result.Items.Select(feature => feature.Id).Should().Equal(1L, 2L);
        result.HasMoreResults.Should().BeFalse();
        await _provider.Received(1).OpenConnectionAsync(Arg.Any<CancellationToken>());
        observer.Count.Should().Be(1);
    }

    [IntegrationTest]
    public async Task QueryAsync_DistinctPage_CountsProjectedTuples()
    {
        using var observer = ObserveSelectCommands();
        var result = await CreateReader().QueryAsync(1,
            new FeatureQuery { Limit = 100, Distinct = true, OutFields = ["tenant"] });

        // The count is of the projected tuples, not the four matching source rows.
        // Keep the distinct count query outside short-first-page source-count reuse.
        result.TotalCount.Should().Be(2);
        result.Items.Select(feature => feature.Attributes["tenant"]).Should().BeEquivalentTo(new[] { "a", "b" });
        result.HasMoreResults.Should().BeFalse();
        await _provider.Received(1).OpenConnectionAsync(Arg.Any<CancellationToken>());
        observer.Count.Should().Be(2);
    }

    private SelectCommandObserver ObserveSelectCommands(Action? onFirstCommandStopped = null)
        => new(_schema, onFirstCommandStopped);

    private PostgresStorageMappedFeatureReader CreateReader() => new(
        _provider,
        new DefaultObjectPoolProvider().Create(new DefaultPooledObjectPolicy<Dictionary<string, object?>>()),
        new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "first-page", Name = "points" },
            Spatial = new MetadataV2ObjectSpatial { GeometryType = MetadataV2GeometryType.Point },
            SchemaFields =
            [
                new() { Name = "id", Type = MetadataV2FieldType.BigInteger },
                new() { Name = "tenant", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String }
            ]
        },
        new FeatureStorageMapping("points", SchemaName: _schema, PrimaryKeyColumn: "id", GeometryColumn: "geom", StorageSrid: 4326),
        connection: null, connectionEncryptionService: null, rlsFilterSource: _rows, fieldMaskSource: _masks);

    private sealed class SelectCommandObserver : IDisposable
    {
        private readonly string _schema;
        private readonly Action? _onFirstCommandStopped;
        private readonly ActivityListener _listener;
        private int _count;

        public SelectCommandObserver(string schema, Action? onFirstCommandStopped)
        {
            _schema = schema;
            _onFirstCommandStopped = onFirstCommandStopped;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = OnActivityStopped,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public int Count => Volatile.Read(ref _count);

        public void Dispose() => _listener.Dispose();

        private void OnActivityStopped(Activity activity)
        {
            if (activity.GetTagItem("db.query.text") is not string text ||
                !text.Contains(_schema, StringComparison.Ordinal) ||
                !text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _count) == 1)
            {
                _onFirstCommandStopped?.Invoke();
            }
        }
    }
}
