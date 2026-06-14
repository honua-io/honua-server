// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using DbUp;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

/// <summary>
/// Overlay-read-at-scale validation for branch versioning (#1511, ADR-0051 open risk #2). The versioned
/// read replaces the base table with
/// <c>(DEFAULT base minus shadowed OIDs) UNION ALL (overlay non-deletes)</c>. ADR-0051 left the behaviour
/// of this shape under a <em>spatial predicate</em> on a <em>large, highly-divergent</em> version
/// unvalidated; the risk is both correctness (the anti-join + UNION must not leak deleted/duplicate rows
/// or evaluate the predicate against the wrong geometry) and the plan (whether the GIST index is still
/// usable across the UNION/anti-join).
///
/// This builds a large DEFAULT table plus a highly-divergent version (thousands of overlay rows) and
/// asserts a bbox read through the real query path returns exactly the right features, then captures the
/// plan of the real builder SQL to confirm the geometry index is used rather than a full table scan.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class BranchVersioningOverlayReadScaleTests : IAsyncLifetime
{
    private const int PointsLayerId = 92001;

    // A large DEFAULT base table placed in a band far OUTSIDE the query window so it is pure anti-join
    // noise, plus a highly-divergent overlay (thousands of shadowing edits, also outside the window). The
    // counts are big enough to make a sequential scan clearly more expensive than the GIST index for a
    // small bbox, so the planner's choice is meaningful.
    private const int BaseNoiseRows = 20_000;
    private const int OverlayNoiseRows = 2_000;

    // Query window B (a small bbox). Crafted features sit clearly inside/outside it (no boundary cases).
    private const double BMinX = -99.50;
    private const double BMinY = 30.50;
    private const double BMaxX = -99.40;
    private const double BMaxY = 30.55;

    // Crafted base features with explicit, known objectids so the overlay can target them precisely.
    private const long Case3DeleteOid = 100_003;   // inside B; branch deletes it
    private const long Case4MoveInOid = 100_004;   // OUTSIDE B; branch moves it INTO B
    private const long Case5MoveOutOid = 100_005;  // inside B; branch moves it OUT of B
    private const long Case6StayInOid = 100_006;   // inside B; branch updates it but it stays IN B
    private const long Case7KeepOid = 100_007;     // inside B; untouched by the branch

    private static readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);
    private static readonly WKBWriter _wkbWriter = new();

    // DEFAULT bbox membership: the crafted base rows that sit inside B before any branch edits.
    private static readonly string[] _expectedDefaultInWindow = ["case3", "case5", "case6-orig", "case7"];

    // Versioned bbox membership: delete removes case3; case5 moves out; case6 stays in (renamed);
    // case4 moves in; case7 stays; the inside-B insert appears; the outside-B insert does not.
    private static readonly string[] _expectedVersionedInWindow =
        ["case1-insert-in", "case4", "case6-updated", "case7"];

    private readonly DatabaseFixtureAdapter _fixture;
    private readonly string _schema;
    private VersionContext _versionContext;
    private Guid _versionId;

    public BranchVersioningOverlayReadScaleTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
        _schema = $"test_bvscale_{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"CREATE SCHEMA {_schema}");

        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = $"{_schema},public"
        };
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionStringBuilder.ToString(), _schema)
            .JournalToPostgresqlTable(_schema, "schema_versions")
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithTransaction()
            .Build();
        var result = upgrader.PerformUpgrade();
        result.Successful.Should().BeTrue(result.Error?.ToString());

        await _fixture.ExecuteAsync($"""
            INSERT INTO honua.layers (layer_id, layer_name, table_schema, table_name, geometry_type, srid)
            VALUES ({PointsLayerId}, 'BV Scale Points', '{_schema}', 'features', 'Point', 4326)
            ON CONFLICT (layer_id) DO UPDATE SET srid = EXCLUDED.srid;
            """);

        await SeedBaseTableAsync();
        await SeedOverlayAsync();
    }

    public async Task DisposeAsync()
    {
        // The version row and its overlay live in the global honua.* tables, not the per-test schema, so
        // remove them explicitly (deleting the version cascades honua.version_edits) before dropping the
        // schema, keeping the shared container clean for the rest of the Database collection.
        if (_versionId != Guid.Empty)
        {
            await _fixture.ExecuteAsync($"DELETE FROM honua.feature_changes WHERE version_id = '{_versionId}'");
            await _fixture.ExecuteAsync($"DELETE FROM honua.version_edits WHERE version_id = '{_versionId}'");
            await _fixture.ExecuteAsync($"DELETE FROM honua.gdb_versions WHERE version_id = '{_versionId}'");
        }

        await _fixture.ExecuteAsync($"DELETE FROM honua.layers WHERE layer_id = {PointsLayerId}");
        await _fixture.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
    }

    /// <summary>
    /// A versioned bbox read over a large base table and a highly-divergent overlay returns exactly the
    /// features whose effective (overlaid) geometry falls in the window: branch inserts inside it appear,
    /// features the branch moved in appear, features it moved out / deleted disappear, and untouched
    /// in-window features remain — with no duplicates and the overlay's attributes, not the base's.
    /// </summary>
    [Fact]
    public async Task VersionedSpatialRead_OnLargeDivergentVersion_OverlaysCorrectlyUnderBbox()
    {
        var store = CreateFeatureStore();
        var bboxFilter = CreateWindowFilter();

        // DEFAULT bbox read is unaffected by the branch: only the original in-window base rows.
        var defaultRows = await store.QueryAsync(
            PointsLayerId,
            new FeatureQuery { SpatialFilter = bboxFilter },
            CancellationToken.None);
        defaultRows.Items.Select(NameOf).OrderBy(n => n, StringComparer.Ordinal)
            .Should().BeEquivalentTo(_expectedDefaultInWindow);

        // Versioned bbox read overlays the branch edits, evaluating the predicate against overlay geometry.
        var versionedRows = await store.QueryAsync(
            PointsLayerId,
            new FeatureQuery { SpatialFilter = bboxFilter, VersionContext = _versionContext },
            CancellationToken.None);
        var versionedNames = versionedRows.Items.Select(NameOf).ToArray();

        versionedNames.Should().OnlyHaveUniqueItems("the overlay UNION/anti-join must not duplicate rows");
        versionedNames.OrderBy(n => n, StringComparer.Ordinal)
            .Should().BeEquivalentTo(_expectedVersionedInWindow);
        versionedNames.Should().Contain("case6-updated")
            .And.NotContain("case6-orig", "the read must reflect the overlay attribute image, not the base");
    }

    /// <summary>
    /// Captures the query plan of the real builder's versioned bbox SQL on the large data set and asserts
    /// the spatial GIST index is used (the bbox predicate is pushed into the base branch of the UNION),
    /// i.e. the versioned read scales with the window, not the table. The plan text is attached to the
    /// assertion so a regression to a full sequential scan is self-documenting.
    /// </summary>
    [Fact]
    public async Task VersionedSpatialRead_Plan_UsesGeometryIndex_NotFullScan()
    {
        var queryBuilder = CreateQueryBuilder();
        var versionedQuery = new FeatureQuery { SpatialFilter = CreateWindowFilter(), VersionContext = _versionContext };
        var built = queryBuilder.BuildSelectQuery(PointsLayerId, versionedQuery);

        await using var connection = await OpenSchemaConnectionAsync();

        // Fresh stats so the planner sizes the base table and the small bbox selectivity correctly.
        await using (var analyze = new NpgsqlCommand($"ANALYZE {_schema}.features; ANALYZE honua.version_edits;", connection))
        {
            await analyze.ExecuteNonQueryAsync();
        }

        // Bind exactly as FeatureDataAccess.AddQueryParameters does for a non-KNN read: $1 = layerId,
        // then the builder's positional WHERE parameters (version id, then the bbox geometry) in order.
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + built.Sql, connection);
        explain.Parameters.AddWithValue(PointsLayerId);
        foreach (var parameter in built.WhereParameters)
        {
            explain.Parameters.AddWithValue(parameter);
        }

        var plan = new System.Text.StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        var planText = plan.ToString();

        // The GIST geometry index (idx_features_geometry*, from migrations 001/018) must appear: that is
        // the proof the bbox predicate reaches the base branch of the overlay UNION and the read scales
        // with the window rather than scanning the whole DEFAULT table.
        planText.Should().Contain(
            "idx_features_geometry",
            $"the versioned overlay read must use the spatial index, not a full scan. Plan:\n{planText}");
    }

    private async Task SeedBaseTableAsync()
    {
        // Bulk DEFAULT noise far outside B (lat ~10), with explicit objectids 1..N.
        await _fixture.ExecuteAsync($"""
            INSERT INTO {_schema}.features (objectid, layer_id, geometry, attributes)
            SELECT g,
                   {PointsLayerId},
                   ST_SetSRID(ST_MakePoint(-100.0 + (g % 1000) * 0.001, 10.0), 4326),
                   jsonb_build_object('name', 'noise-base-' || g)
            FROM generate_series(1, {BaseNoiseRows}) AS g;
            """);

        // Crafted, individually-known base rows.
        await _fixture.ExecuteAsync($"""
            INSERT INTO {_schema}.features (objectid, layer_id, geometry, attributes) VALUES
                ({Case3DeleteOid}, {PointsLayerId}, ST_SetSRID(ST_MakePoint(-99.45, 30.52), 4326), jsonb_build_object('name', 'case3')),
                ({Case4MoveInOid}, {PointsLayerId}, ST_SetSRID(ST_MakePoint(-98.00, 30.00), 4326), jsonb_build_object('name', 'case4')),
                ({Case5MoveOutOid}, {PointsLayerId}, ST_SetSRID(ST_MakePoint(-99.46, 30.53), 4326), jsonb_build_object('name', 'case5')),
                ({Case6StayInOid}, {PointsLayerId}, ST_SetSRID(ST_MakePoint(-99.47, 30.54), 4326), jsonb_build_object('name', 'case6-orig')),
                ({Case7KeepOid}, {PointsLayerId}, ST_SetSRID(ST_MakePoint(-99.48, 30.51), 4326), jsonb_build_object('name', 'case7'));
            """);

        // Move the identity sequence past the crafted ids so writer-created inserts get fresh objectids.
        await _fixture.ExecuteAsync(
            $"SELECT setval(pg_get_serial_sequence('{_schema}.features', 'objectid'), {Case7KeepOid + 1000}, true);");
    }

    private async Task SeedOverlayAsync()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        // honua.gdb_versions/version_edits are global (only `features` is per-test-schema), and the
        // (owner, name) index is unique across the shared container, so name the version uniquely.
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest($"scaleoverlay_{Guid.NewGuid():N}", "sde", VersionAccess.Public),
            CancellationToken.None);
        _versionContext = VersionContext.ForVersion(version);
        _versionId = version.VersionId;

        // Crafted overlay edits through the real writer path.
        var batch = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(
                BuildFeature("case1-insert-in", -99.43, 30.525),
                BuildFeature("case2-insert-out", -98.50, 30.00)),
            updates: ImmutableArray.Create(
                BuildFeature("case4", -99.44, 30.52, Case4MoveInOid),
                BuildFeature("case5", -98.20, 30.00, Case5MoveOutOid),
                BuildFeature("case6-updated", -99.47, 30.545, Case6StayInOid)),
            deletes: ImmutableArray.Create(Case3DeleteOid),
            rollbackOnFailure: true,
            versionContext: _versionContext);

        var editResult = await store.ApplyEditsAsync(PointsLayerId, batch, CancellationToken.None);
        editResult.WasRolledBack.Should().BeFalse();

        // Make the version highly divergent: thousands of overlay updates shadowing base noise rows
        // (operation 2), all far outside B. Seeded directly so the test exercises the read at scale
        // without paying thousands of write round-trips; the overlay trigger stamps branch_gen.
        await _fixture.ExecuteAsync($"""
            INSERT INTO honua.version_edits (version_id, layer_id, objectid, operation, geometry, attributes, branch_gen)
            SELECT '{version.VersionId}',
                   {PointsLayerId},
                   g,
                   2,
                   ST_SetSRID(ST_MakePoint(-98.0, 9.0), 4326),
                   jsonb_build_object('name', 'noise-upd-' || g),
                   0
            FROM generate_series(1, {OverlayNoiseRows}) AS g;
            """);
    }

    private SpatialFilter CreateWindowFilter()
    {
        var ring = _geometryFactory.CreatePolygon(
        [
            new Coordinate(BMinX, BMinY),
            new Coordinate(BMaxX, BMinY),
            new Coordinate(BMaxX, BMaxY),
            new Coordinate(BMinX, BMaxY),
            new Coordinate(BMinX, BMinY),
        ]);
        ring.SRID = 4326;
        return SpatialFilter.Create(_wkbWriter.Write(ring), SpatialRelationship.Intersects, srid: 4326);
    }

    private static string NameOf(Feature feature)
        => feature.Attributes.TryGetValue("name", out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static Feature BuildFeature(string name, double x, double y, long id = 0)
    {
        var point = _geometryFactory.CreatePoint(new Coordinate(x, y));
        point.SRID = 4326;
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("name", name);
        return new Feature { Id = id, Geometry = _wkbWriter.Write(point), Attributes = attributes };
    }

    private FeatureQueryBuilder CreateQueryBuilder()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new Microsoft.Extensions.ObjectPool.StringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(stringBuilderPool, new GeometryProcessor(), _schema);
    }

    private PostgresFeatureStoreRefactored CreateFeatureStore()
    {
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schema);
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new Microsoft.Extensions.ObjectPool.StringBuilderPooledObjectPolicy());
        var dictionaryPool = poolProvider.Create(new DictionaryPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var cacheManager = new FeatureCacheManager(connectionProvider, NullLogger<FeatureCacheManager>.Instance, _schema);
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor, _schema);
        var dataAccess = new FeatureDataAccess(new FeatureDataAccessDependencies(
            connectionProvider,
            geometryProcessor,
            cacheManager,
            dictionaryPool,
            StatementCache: null,
            Logger: NullLogger<FeatureDataAccess>.Instance,
            PerformanceOptions: null,
            LimitsOptions: null,
            PerformanceMonitor: null,
            SchemaName: _schema));

        return new PostgresFeatureStoreRefactored(
            queryBuilder,
            dataAccess,
            cacheManager,
            connectionProvider,
            dictionaryPool,
            connectionEncryptionService: null,
            filterExpressionService: null);
    }

    private PostgresVersionManager CreateVersionManager()
    {
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schema);
        return new PostgresVersionManager(connectionProvider, _schema);
    }

    private async Task<NpgsqlConnection> OpenSchemaConnectionAsync()
    {
        var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SET search_path TO {_schema}, public;";
            await cmd.ExecuteNonQueryAsync();
        }

        return connection;
    }
}
