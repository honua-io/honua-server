// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Reflection;
using DbUp;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Server.Tests.Features.FeatureStore;

/// <summary>
/// Branch-versioning async reconcile/post hardening tests (#1553). Proves the durable (service,version)
/// lock serializes concurrent reconcile/post (surfacing a clear lock-contended signal), that the async
/// job runner makes reconcile/post pollable end to end, and that the durable conflict records survive a
/// "restart" so a re-run is idempotent/resumable. Uses a real PostGIS schema; the lock is the single-node
/// in-process implementation (production substitutes the Redis-backed lock without changing behavior).
/// The DEFAULT path is never exercised by these branch-version-only flows.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class BranchVersioningAsyncReconcileTests : IAsyncLifetime
{
    private const int PointsLayerId = 92001;
    private static readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);
    private static readonly WKBWriter _wkbWriter = new();

    private readonly DatabaseFixtureAdapter _fixture;
    private readonly string _schema;
    private readonly InProcessVersionLock _sharedLock = new();

    public BranchVersioningAsyncReconcileTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
        _schema = $"test_bvasync_{Guid.NewGuid():N}";
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
            VALUES ({PointsLayerId}, 'BV Async Points', '{_schema}', 'features', 'Point', 4326)
            ON CONFLICT (layer_id) DO UPDATE SET srid = EXCLUDED.srid;
            """);
    }

    public async Task DisposeAsync()
    {
        await _fixture.ExecuteAsync($"DROP SCHEMA {_schema} CASCADE");
    }

    [Fact]
    public async Task Reconcile_WhileVersionLockHeld_ThrowsLockContended()
    {
        var versionManager = CreateVersionManager();
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("LockHeld", "sde", VersionAccess.Public), CancellationToken.None);

        // Hold the same (schema, version) lock another in-flight reconcile/post would take, then prove a
        // competing reconcile fails fast rather than running concurrently.
        await using var held = await _sharedLock.TryAcquireAsync(
            _schema, version.VersionId, TimeSpan.FromMinutes(1), CancellationToken.None);
        held.Should().NotBeNull();

        var act = async () => await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        (await act.Should().ThrowAsync<VersionLockedException>())
            .Which.VersionId.Should().Be(version.VersionId);
    }

    [Fact]
    public async Task ConcurrentReconcileAndPost_AreSerializedByTheVersionLock()
    {
        var versionManager = CreateVersionManager();
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("Serialize", "sde", VersionAccess.Public), CancellationToken.None);

        // While a post holds the lock (simulated by holding it directly), a concurrent reconcile is
        // rejected; once released, the reconcile proceeds. This is the serialization guarantee.
        var handle = await _sharedLock.TryAcquireAsync(
            _schema, version.VersionId, TimeSpan.FromMinutes(1), CancellationToken.None);
        handle.Should().NotBeNull();

        var blocked = async () => await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        await blocked.Should().ThrowAsync<VersionLockedException>();

        await handle!.DisposeAsync();

        var result = await versionManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        result.CanPost.Should().BeTrue("the lock is free once the prior holder releases it");
    }

    [Fact]
    public async Task AsyncReconcileJob_RunsToCompletion_AndIsPollable()
    {
        await using var provider = BuildServiceProvider();
        var runner = provider.GetRequiredService<IVersionJobRunner>();

        var store = CreateFeatureStore();
        var versionManager = CreateVersionManager();
        await store.CreateAsync(PointsLayerId, BuildFeature("Async-Base", 1.0, 1.0), CancellationToken.None);
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("AsyncJob", "sde", VersionAccess.Public), CancellationToken.None);

        var job = await runner.StartReconcileAsync(
            "svc", version.VersionId, VersionReconcilePolicy.None, CancellationToken.None);
        job.Status.Should().Be(VersionJobStatus.Pending);

        var terminal = await PollJobAsync(runner, job.JobId);
        terminal.Status.Should().Be(VersionJobStatus.Succeeded);
        terminal.CanPost.Should().BeTrue("a version with no overlapping DEFAULT edits reconciles cleanly");
        terminal.ConflictCount.Should().Be(0);
    }

    [Fact]
    public async Task AsyncReconcileJob_LockContended_TerminatesAsLockContended()
    {
        await using var provider = BuildServiceProvider();
        var runner = provider.GetRequiredService<IVersionJobRunner>();
        var versionManager = CreateVersionManager();
        var version = await versionManager.CreateAsync(
            new CreateVersionRequest("AsyncContended", "sde", VersionAccess.Public), CancellationToken.None);

        // Hold the lock the job's reconcile will try to take so the job terminates as lock-contended.
        await using var held = await _sharedLock.TryAcquireAsync(
            _schema, version.VersionId, TimeSpan.FromMinutes(1), CancellationToken.None);
        held.Should().NotBeNull();

        var job = await runner.StartReconcileAsync(
            "svc", version.VersionId, VersionReconcilePolicy.None, CancellationToken.None);

        var terminal = await PollJobAsync(runner, job.JobId);
        terminal.Status.Should().Be(VersionJobStatus.LockContended);
    }

    [Fact]
    public async Task Reconcile_AfterRestart_IsIdempotentAndResumesPendingConflicts()
    {
        var store = CreateFeatureStore();
        var firstManager = CreateVersionManager();

        var feature = await store.CreateAsync(PointsLayerId, BuildFeature("Resume-Base", 2.0, 2.0), CancellationToken.None);
        var version = await firstManager.CreateAsync(
            new CreateVersionRequest("Resume", "sde", VersionAccess.Public), CancellationToken.None);

        await store.ApplyEditsAsync(
            PointsLayerId,
            FeatureEditBatch.Create(
                updates: ImmutableArray.Create(BuildFeature("Resume-Branch", 2.5, 2.5, feature.Id)),
                versionContext: VersionContext.ForVersion(version)),
            CancellationToken.None);
        await store.UpdateAsync(PointsLayerId, BuildFeature("Resume-Default", 2.9, 2.9, feature.Id), CancellationToken.None);

        var first = await firstManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        first.CanPost.Should().BeFalse();
        first.Conflicts.Should().ContainSingle(c => c.ObjectId == feature.Id);

        // Simulate a restart: a fresh manager instance reads the durable pending conflict the prior run
        // persisted, and a re-run recomputes the same pending set (idempotent) without double-counting.
        var secondManager = CreateVersionManager();
        var pendingAfterRestart = await secondManager.GetPendingConflictsAsync(version.VersionId, CancellationToken.None);
        pendingAfterRestart.Should().ContainSingle(c => c.ObjectId == feature.Id);

        var second = await secondManager.ReconcileAsync(version.VersionId, cancellationToken: CancellationToken.None);
        second.CanPost.Should().BeFalse();
        second.Conflicts.Should().ContainSingle(c => c.ObjectId == feature.Id);

        var pendingAfterRerun = await secondManager.GetPendingConflictsAsync(version.VersionId, CancellationToken.None);
        pendingAfterRerun.Should().HaveCount(1, "a re-run refreshes rather than duplicates the pending set");
    }

    private async Task<VersionJob> PollJobAsync(IVersionJobRunner runner, Guid jobId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await runner.GetJobAsync(jobId, CancellationToken.None);
            job.Should().NotBeNull();
            if (job!.Status is not (VersionJobStatus.Pending or VersionJobStatus.Running))
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Version job {jobId} did not reach a terminal state.");
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVersionLock>(_sharedLock);
        services.AddSingleton<IVersionJobStore, InMemoryVersionJobStore>();
        services.AddScoped<IVersionManager>(_ => CreateVersionManager());
        services.AddSingleton<IVersionJobRunner, VersionJobRunner>();
        return services.BuildServiceProvider();
    }

    private static Feature BuildFeature(string name, double x, double y, long id = 0)
    {
        var point = _geometryFactory.CreatePoint(new Coordinate(x, y));
        point.SRID = 4326;
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("name", name);
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
        return new PostgresVersionManager(connectionProvider, _schema, _sharedLock);
    }
}
