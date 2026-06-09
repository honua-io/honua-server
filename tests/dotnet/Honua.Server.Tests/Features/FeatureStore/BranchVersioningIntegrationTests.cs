// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
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
/// Branch-versioning Slice 1 integration tests (#1272 Track B, ADR-0051). Proves the readable/editable
/// version core against a real PostGIS schema: a version's edits are isolated from DEFAULT, a versioned
/// read overlays them, the DEFAULT read is unaffected, the ancestor base image is captured on first
/// touch, and the DEFAULT (null version) read/edit path is byte-identical.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class BranchVersioningIntegrationTests : IAsyncLifetime
{
    private const int PointsLayerId = 91001;
    private static readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);
    private static readonly WKBWriter _wkbWriter = new();
    private static readonly string[] _expectedDefaultNames = ["Original-1", "Original-2"];
    private static readonly string[] _expectedVersionedNames = ["Branch-New", "Branch-Updated"];

    private readonly DatabaseFixtureAdapter _fixture;
    private readonly string _schema;

    public BranchVersioningIntegrationTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
        _schema = $"test_bv_{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"CREATE SCHEMA {_schema}");

        // Apply the full embedded migration set into the isolated schema so features + the honua.*
        // versioning tables (gdb_versions, version_edits, feature_changes, sync_generation) and their
        // triggers all exist exactly as production runs them.
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

        // Register the test layer so the feature store resolves its SRID (4326) and stamps written
        // geometries; without it the geography GIST index expression (ST_Transform(...,4326)) rejects
        // the SRID-0 WKB the test produces.
        await _fixture.ExecuteAsync($"""
            INSERT INTO honua.layers (layer_id, layer_name, table_schema, table_name, geometry_type, srid)
            VALUES ({PointsLayerId}, 'BV Points', '{_schema}', 'features', 'Point', 4326)
            ON CONFLICT (layer_id) DO UPDATE SET srid = EXCLUDED.srid;
            """);
    }

    public async Task DisposeAsync()
    {
        await _fixture.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
    }

    [Fact]
    public async Task VersionedEdit_IsIsolatedFromDefault_AndVersionedReadOverlaysIt()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        // DEFAULT baseline: two features.
        var feature1 = await store.CreateAsync(PointsLayerId, BuildFeature("Original-1", 1.0, 1.0), CancellationToken.None);
        var feature2 = await store.CreateAsync(PointsLayerId, BuildFeature("Original-2", 2.0, 2.0), CancellationToken.None);

        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("QA", "sde", VersionAccess.Public), CancellationToken.None);
        var versionContext = VersionContext.ForVersion(version);

        // Edit inside the version: update feature1, delete feature2, create a new branch-only feature.
        var batch = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(BuildFeature("Branch-New", 3.0, 3.0)),
            updates: ImmutableArray.Create(BuildFeature("Branch-Updated", 1.5, 1.5, feature1.Id)),
            deletes: ImmutableArray.Create(feature2.Id),
            rollbackOnFailure: true,
            versionContext: versionContext);

        var editResult = await store.ApplyEditsAsync(PointsLayerId, batch, CancellationToken.None);
        editResult.WasRolledBack.Should().BeFalse();
        editResult.CreatedCount.Should().Be(1);
        editResult.UpdatedCount.Should().Be(1);
        editResult.DeletedCount.Should().Be(1);

        // DEFAULT read is unaffected: still the two originals.
        var defaultRows = await store.QueryAsync(PointsLayerId, new FeatureQuery(), CancellationToken.None);
        var defaultNames = defaultRows.Items.Select(NameOf).OrderBy(n => n).ToArray();
        defaultNames.Should().BeEquivalentTo(_expectedDefaultNames);

        // Versioned read overlays the branch edits: updated f1, branch-new, f2 deleted.
        var versionedRows = await store.QueryAsync(
            PointsLayerId,
            new FeatureQuery { VersionContext = versionContext },
            CancellationToken.None);
        var versionedNames = versionedRows.Items.Select(NameOf).OrderBy(n => n).ToArray();
        versionedNames.Should().BeEquivalentTo(_expectedVersionedNames);
    }

    [Fact]
    public async Task VersionedFirstTouch_CapturesDefaultBaseImage()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("Base-Capture", 4.0, 4.0), CancellationToken.None);

        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("Capture", "sde", VersionAccess.Public), CancellationToken.None);

        var batch = FeatureEditBatch.Create(
            updates: ImmutableArray.Create(BuildFeature("Mutated", 4.5, 4.5, feature.Id)),
            versionContext: VersionContext.ForVersion(version));
        await store.ApplyEditsAsync(PointsLayerId, batch, CancellationToken.None);

        // The first branch touch must snapshot the then-current DEFAULT row into base_*.
        await using var connection = await OpenSchemaConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT base_attributes IS NOT NULL, base_geometry IS NOT NULL FROM honua.version_edits WHERE version_id = @v AND layer_id = @l AND objectid = @o",
            connection);
        command.Parameters.AddWithValue("v", version.VersionId);
        command.Parameters.AddWithValue("l", PointsLayerId);
        command.Parameters.AddWithValue("o", feature.Id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetBoolean(0).Should().BeTrue("base attributes are captured on first branch touch");
        reader.GetBoolean(1).Should().BeTrue("base geometry is captured on first branch touch");
    }

    [Fact]
    public async Task VersionedChange_IsTaggedInChangeLog_DefaultChangeIsNot()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        // A DEFAULT edit records a NULL-version change (byte-identical change-tracking).
        var defaultFeature = await store.CreateAsync(PointsLayerId, BuildFeature("Default-Edit", 5.0, 5.0), CancellationToken.None);

        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("Tagged", "sde", VersionAccess.Public), CancellationToken.None);

        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeature("Tagged-Edit", 5.5, 5.5, defaultFeature.Id)),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);

        await using var connection = await OpenSchemaConnectionAsync();
        await using var versioned = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.feature_changes WHERE version_id = @v AND layer_id = @l", connection);
        versioned.Parameters.AddWithValue("v", version.VersionId);
        versioned.Parameters.AddWithValue("l", PointsLayerId);
        var versionedCount = Convert.ToInt64(await versioned.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        versionedCount.Should().BeGreaterThan(0, "the overlay trigger tags version edits in the change log");

        await using var defaultTagged = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.feature_changes WHERE version_id IS NULL AND layer_id = @l", connection);
        defaultTagged.Parameters.AddWithValue("l", PointsLayerId);
        var defaultNullVersionCount = Convert.ToInt64(await defaultTagged.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        defaultNullVersionCount.Should().BeGreaterThan(0, "DEFAULT edits stay NULL-version (byte-identical change tracking)");
    }

    [Fact]
    public async Task ResolveAsync_DefaultSentinelAndAbsent_ResolveToDefault()
    {
        var versionManager = CreateVersionManager();

        (await versionManager.ResolveAsync(null, CancellationToken.None))!.Value.IsDefault.Should().BeTrue();
        (await versionManager.ResolveAsync("", CancellationToken.None))!.Value.IsDefault.Should().BeTrue();
        (await versionManager.ResolveAsync("SDE.DEFAULT", CancellationToken.None))!.Value.IsDefault.Should().BeTrue();
        (await versionManager.ResolveAsync("does.not.exist", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CreateResolveDelete_VersionLifecycle_RoundTrips()
    {
        var versionManager = CreateVersionManager();

        var created = await versionManager.CreateAsync(
            new CreateVersionRequest("Lifecycle", "sde", VersionAccess.Protected, Description: "round trip"),
            CancellationToken.None);
        created.CommonAncestorGeneration.Should().BeGreaterThanOrEqualTo(0);

        var resolved = await versionManager.ResolveAsync("sde.Lifecycle", CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved!.Value.VersionId.Should().Be(created.VersionId);
        resolved.Value.IsDefault.Should().BeFalse();

        (await versionManager.DeleteAsync(created.VersionId, CancellationToken.None)).Should().BeTrue();
        (await versionManager.ResolveAsync("sde.Lifecycle", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Reconcile_NoOverlappingDefaultEdits_IsCleanAndCanPost()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("Recon-Base", 6.0, 6.0), CancellationToken.None);
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("ReconClean", "sde", VersionAccess.Public), CancellationToken.None);

        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeature("Recon-Branch", 6.5, 6.5, feature.Id)),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);

        var result = await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);

        result.CanPost.Should().BeTrue();
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_OverlappingDefaultEdit_DetectsConflictAndBlocksPost()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("Conflict-Base", 7.0, 7.0), CancellationToken.None);
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("ReconConflict", "sde", VersionAccess.Public), CancellationToken.None);

        // Branch edit then a DEFAULT edit on the same feature since the merge base => conflict.
        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeature("Conflict-Branch", 7.5, 7.5, feature.Id)),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await store.UpdateAsync(PointsLayerId, BuildFeature("Conflict-Default", 7.9, 7.9, feature.Id), CancellationToken.None);

        var reconcile = await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        reconcile.CanPost.Should().BeFalse();
        reconcile.Conflicts.Should().ContainSingle(c => c.ObjectId == feature.Id);

        var post = await versionManager.PostAsync(version.VersionId, CancellationToken.None);
        post.Posted.Should().BeFalse();
        post.BlockedByConflicts.Should().BeTrue();
    }

    [Fact]
    public async Task Post_CleanVersion_ReflectsBranchEditsInDefault()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        var keep = await store.CreateAsync(PointsLayerId, BuildFeature("Keep", 8.0, 8.0), CancellationToken.None);
        var drop = await store.CreateAsync(PointsLayerId, BuildFeature("Drop", 8.1, 8.1), CancellationToken.None);

        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("PostClean", "sde", VersionAccess.Public), CancellationToken.None);

        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                creates: ImmutableArray.Create(BuildFeature("Posted-New", 8.2, 8.2)),
                updates: ImmutableArray.Create(BuildFeature("Keep-Updated", 8.3, 8.3, keep.Id)),
                deletes: ImmutableArray.Create(drop.Id),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);

        var reconcile = await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        reconcile.CanPost.Should().BeTrue();

        var post = await versionManager.PostAsync(version.VersionId, CancellationToken.None);
        post.Posted.Should().BeTrue();
        post.BlockedByConflicts.Should().BeFalse();

        // DEFAULT now reflects the branch: Drop is gone, Keep-Updated replaces Keep, Posted-New is present.
        var defaultRows = await store.QueryAsync(PointsLayerId, new FeatureQuery(), CancellationToken.None);
        var names = defaultRows.Items.Select(NameOf).OrderBy(n => n).ToArray();
        names.Should().Contain("Keep-Updated");
        names.Should().Contain("Posted-New");
        names.Should().NotContain("Drop");
        names.Should().NotContain("Keep");
    }

    [Fact]
    public async Task Reconcile_DisjointFieldEdits_AutoMergesWithoutConflict()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        // Base feature with two attribute fields.
        var feature = await store.CreateAsync(
            PointsLayerId, BuildFeatureWithFields(9.0, 9.0, ("name", "Disjoint"), ("status", "open")), CancellationToken.None);
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("Disjoint", "sde", VersionAccess.Public), CancellationToken.None);

        // Branch edits only "name"; DEFAULT edits only "status" => disjoint, auto-mergeable.
        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeatureWithFields(9.0, 9.0, feature.Id, ("name", "Branch"), ("status", "open"))),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await store.UpdateAsync(
            PointsLayerId, BuildFeatureWithFields(9.0, 9.0, feature.Id, ("name", "Disjoint"), ("status", "closed")), CancellationToken.None);

        var reconcile = await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        reconcile.Conflicts.Should().BeEmpty("disjoint field edits auto-merge");
        reconcile.CanPost.Should().BeTrue();

        var post = await versionManager.PostAsync(version.VersionId, CancellationToken.None);
        post.Posted.Should().BeTrue();

        // DEFAULT carries BOTH the branch's name and DEFAULT's status.
        var rows = await store.QueryAsync(PointsLayerId, new FeatureQuery(), CancellationToken.None);
        var merged = rows.Items.Single(f => f.Id == feature.Id);
        FieldValue(merged, "name").Should().Be("Branch");
        FieldValue(merged, "status").Should().Be("closed");
    }

    [Fact]
    public async Task Reconcile_OverlappingFieldEdits_ReportsThreeWayConflict()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        var feature = await store.CreateAsync(
            PointsLayerId, BuildFeatureWithFields(10.0, 10.0, ("name", "Base"), ("status", "open")), CancellationToken.None);
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("Overlap", "sde", VersionAccess.Public), CancellationToken.None);

        // Both edit "status" => overlapping field => genuine conflict with a 3-way report.
        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeatureWithFields(10.0, 10.0, feature.Id, ("name", "Base"), ("status", "branch"))),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await store.UpdateAsync(
            PointsLayerId, BuildFeatureWithFields(10.0, 10.0, feature.Id, ("name", "Base"), ("status", "default")), CancellationToken.None);

        var reconcile = await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        reconcile.CanPost.Should().BeFalse();
        var conflict = reconcile.Conflicts.Should().ContainSingle(c => c.ObjectId == feature.Id).Subject;
        conflict.ConflictType.Should().Be(ReplicaConflictType.Attribute);
        conflict.BaseAttributesJson.Should().Contain("open");
        conflict.DefaultAttributesJson.Should().Contain("default");
        conflict.VersionAttributesJson.Should().Contain("branch");

        var statusDiff = conflict.FieldDiffs.Should().ContainSingle(d => d.Name == "status").Subject;
        statusDiff.Base.Should().Contain("open");
        statusDiff.Default.Should().Contain("default");
        statusDiff.Version.Should().Contain("branch");

        // The pending conflict is persisted and retrievable for a manual-resolution UI.
        var pending = await versionManager.GetPendingConflictsAsync(version.VersionId, CancellationToken.None);
        pending.Should().ContainSingle(c => c.ObjectId == feature.Id);
    }

    [Theory]
    [InlineData(VersionReconcilePolicy.VersionWins, "branch")]
    [InlineData(VersionReconcilePolicy.DefaultWins, "default")]
    [InlineData(VersionReconcilePolicy.LastWriteWins, "default")]
    public async Task Reconcile_WithPolicy_AutoResolvesAndPosts(VersionReconcilePolicy policy, string expectedStatus)
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        var feature = await store.CreateAsync(
            PointsLayerId, BuildFeatureWithFields(11.0, 11.0, ("name", "Base"), ("status", "open")), CancellationToken.None);
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest($"Policy_{policy}", "sde", VersionAccess.Public), CancellationToken.None);

        // Branch edits status first, then DEFAULT edits status later (DEFAULT is the last write).
        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeatureWithFields(11.0, 11.0, feature.Id, ("name", "Base"), ("status", "branch"))),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await store.UpdateAsync(
            PointsLayerId, BuildFeatureWithFields(11.0, 11.0, feature.Id, ("name", "Base"), ("status", "default")), CancellationToken.None);

        var reconcile = await versionManager.ReconcileAsync(version.VersionId, policy, CancellationToken.None);
        reconcile.CanPost.Should().BeTrue("policy auto-resolves the conflict");
        reconcile.AutoResolvedCount.Should().Be(1);
        reconcile.Conflicts.Should().BeEmpty();

        var post = await versionManager.PostAsync(version.VersionId, CancellationToken.None);
        post.Posted.Should().BeTrue();

        var rows = await store.QueryAsync(PointsLayerId, new FeatureQuery(), CancellationToken.None);
        var resolved = rows.Items.Single(f => f.Id == feature.Id);
        FieldValue(resolved, "status").Should().Be(expectedStatus);
    }

    [Fact]
    public async Task ResolveConflicts_TakeVersion_ClearsConflictAndPostsVersionImage()
    {
        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();

        var feature = await store.CreateAsync(
            PointsLayerId, BuildFeatureWithFields(12.0, 12.0, ("name", "Base"), ("status", "open")), CancellationToken.None);
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("ManualResolve", "sde", VersionAccess.Public), CancellationToken.None);

        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeatureWithFields(12.0, 12.0, feature.Id, ("name", "Base"), ("status", "branch"))),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await store.UpdateAsync(
            PointsLayerId, BuildFeatureWithFields(12.0, 12.0, feature.Id, ("name", "Base"), ("status", "default")), CancellationToken.None);

        var reconcile = await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        reconcile.CanPost.Should().BeFalse();

        // Manual resolution: take the version side. Post is blocked until resolved.
        var blocked = await versionManager.PostAsync(version.VersionId, CancellationToken.None);
        blocked.BlockedByConflicts.Should().BeTrue();

        var resolution = new VersionConflictResolution(PointsLayerId, feature.Id, VersionConflictResolutionChoice.TakeVersion);
        var outcome = await versionManager.ResolveConflictsAsync(
            version.VersionId, new[] { resolution }, CancellationToken.None);
        outcome.Resolved.Should().Be(1);
        outcome.Remaining.Should().Be(0);
        outcome.CanPost.Should().BeTrue();

        (await versionManager.GetPendingConflictsAsync(version.VersionId, CancellationToken.None))
            .Should().BeEmpty();

        var post = await versionManager.PostAsync(version.VersionId, CancellationToken.None);
        post.Posted.Should().BeTrue();

        var rows = await store.QueryAsync(PointsLayerId, new FeatureQuery(), CancellationToken.None);
        FieldValue(rows.Items.Single(f => f.Id == feature.Id), "status").Should().Be("branch");
    }

    private static string NameOf(Feature feature)
        => feature.Attributes.TryGetValue("name", out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static string? FieldValue(Feature feature, string field)
        => feature.Attributes.TryGetValue(field, out var value) ? value?.ToString() : null;

    private static Feature BuildFeature(string name, double x, double y, long id = 0)
    {
        var point = _geometryFactory.CreatePoint(new Coordinate(x, y));
        point.SRID = 4326;
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("name", name);
        return new Feature { Id = id, Geometry = _wkbWriter.Write(point), Attributes = attributes };
    }

    private static Feature BuildFeatureWithFields(double x, double y, params (string Key, object? Value)[] fields)
        => BuildFeatureWithFields(x, y, 0, fields);

    private static Feature BuildFeatureWithFields(double x, double y, long id, params (string Key, object? Value)[] fields)
    {
        var point = _geometryFactory.CreatePoint(new Coordinate(x, y));
        point.SRID = 4326;
        var attributes = ImmutableDictionary<string, object?>.Empty;
        foreach (var (key, value) in fields)
        {
            attributes = attributes.SetItem(key, value);
        }

        return new Feature { Id = id, Geometry = _wkbWriter.Write(point), Attributes = attributes };
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
