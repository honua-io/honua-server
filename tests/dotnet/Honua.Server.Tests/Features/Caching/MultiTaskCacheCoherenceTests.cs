// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Metadata;
using Honua.Infrastructure.Caching;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Caching;

// Two server tasks on one database, each with its own in-process catalog snapshot and output cache and nothing
// broadcast between them: a change committed through task A must govern task B's very next answer.
[Collection("Database")]
[Protocol(TestProtocols.OgcApiFeatures)]
[Operation(Operations.Cache)]
public sealed class MultiTaskCacheCoherenceTests : IAsyncLifetime
{
    private const string CollectionPath = "/ogc/features/collections/0";
    private const string TilePath = "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0";
    private const int LayerIndex = 0;

    private readonly string _environment = $"twotask-{Guid.NewGuid():N}";
    private WebAppFixture _taskA = null!;
    private WebAppFixture _taskB = null!;

    public async Task InitializeAsync()
    {
        _taskA = NewTaskHost();
        await _taskA.InitializeAsync();
        await InstallCatalogTablesAsync(_taskA);
        var seed = _taskA.GetService<TestMetadataV2GraphProvider>().GetCurrentForSchema(_taskA.CurrentSchema).Graph;
        await SaveCatalogAsync(_taskA, seed with { Environment = _environment, Revision = 1 }, expectedEtag: null);

        _taskB = NewTaskHost().ShareSchemaWith(_taskA);
        await _taskB.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _taskB.DisposeAsync();
        await _taskA.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task CatalogChangeOnTaskA_ReachesTaskBCachedCollection_OnItsNextRequest()
    {
        using var clientB = _taskB.CreateClient();
        await ReadUntilReplayedAsync(clientB, CollectionPath);

        await ChangeLayerAsync(_taskA, resource => resource with
        {
            Metadata = resource.Metadata with { Description = "Described on task A" }
        });

        using var afterChange = await clientB.GetAsync(CollectionPath);
        afterChange.StatusCode.Should().Be(HttpStatusCode.OK);
        (await afterChange.Content.ReadAsStringAsync()).Should().Contain("Described on task A");
        CacheStatus(afterChange).Should().StartWith("Honua; fwd=miss");
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task LayerMadePrivateOnTaskA_TaskBStopsReplayingItsCachedCollectionAndTile()
    {
        using var clientB = _taskB.CreateClient();
        await ReadUntilReplayedAsync(clientB, CollectionPath);
        await ReadUntilReplayedAsync(clientB, TilePath);

        await ChangeLayerAsync(_taskA, resource => resource with { AccessPolicy = new AccessPolicy { AllowAnonymous = false } });

        using var collection = await clientB.GetAsync(CollectionPath);
        using var tile = await clientB.GetAsync(TilePath);
        collection.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        tile.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    // The first read renders and stores; the second must come from task B's own cache, or the test proves nothing.
    private static async Task ReadUntilReplayedAsync(HttpClient client, string path)
    {
        using var first = await client.GetAsync(path);
        first.StatusCode.Should().Be(HttpStatusCode.OK, "{0} must be public before the change", path);
        using var second = await client.GetAsync(path);
        CacheStatus(second).Should().StartWith("Honua; hit", "{0} must be replayed from task B's cache before the change", path);
    }

    private static string CacheStatus(HttpResponseMessage response)
        => response.Headers.TryGetValues(OutputCacheStatus.HeaderName, out var values) ? string.Join(", ", values) : "<none>";

    private WebAppFixture NewTaskHost()
    {
        var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder => builder.UseSetting("HONUA_DEV_AUTH", "false"));
        fixture.ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.RemoveAll<IMetadataV2GraphStore>();
            services.AddScoped<IMetadataV2GraphStore>(provider => new PostgresMetadataV2GraphStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                _environment,
                provider.GetRequiredService<IDatabaseSchemaGuard>(),
                schemaName: "honua",
                cacheInvalidator: provider.GetRequiredService<IMetadataV2GraphCacheInvalidator>()));
            services.AddScoped<IMetadataV2GraphProvider>(provider =>
            {
                var store = provider.GetRequiredService<IMetadataV2GraphStore>();
                return new CachingMetadataV2GraphProvider(
                    store,
                    provider.GetRequiredService<MetadataV2GraphSnapshotCache>(),
                    _environment,
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    static scope => scope.GetRequiredService<IMetadataV2GraphStore>(),
                    new IntervalMetadataV2GraphRevisionProbe(
                        (IMetadataV2GraphRevisionProbe)store,
                        provider.GetRequiredService<MetadataV2GraphRevisionProbeSchedule>(),
                        // Production polls at most every 2 s for reads; zero makes "the next request" exact for the test.
                        new MetadataV2GraphRevalidation(TimeSpan.Zero, RequireCurrentRevision: false)));
            });
            services.TryAddSingleton(static provider => new MetadataV2GraphRevisionProbeSchedule(TimeProvider.System));
        });
        return fixture;
    }

    // Test hosts skip DbUp; install the production catalog tables the real store writes.
    private static async Task InstallCatalogTablesAsync(WebAppFixture fixture)
    {
        var assembly = typeof(ServerCoreSchemaMigrations).Assembly;
        var migrationName = ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration;
        await using var stream = assembly.GetManifestResourceStream(migrationName)
            ?? throw new InvalidOperationException($"Embedded migration '{migrationName}' was not found.");
        using var reader = new StreamReader(stream);
        var ddl = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", "honua", StringComparison.Ordinal);
        await fixture.Postgres.ExecuteDdlUnderLockAsync(ddl);
    }

    private static async Task ChangeLayerAsync(WebAppFixture task, Func<MetadataV2Resource, MetadataV2Resource> change)
    {
        await using var scope = task.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
        var current = await store.GetCurrentAsync();
        var layerResourceIds = current.Graph.Publications
            .Where(publication => publication.LayerIndex == LayerIndex)
            .Select(publication => publication.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
        var resources = current.Graph.Resources
            .Select(resource => layerResourceIds.Contains(resource.Metadata.Id) ? change(resource) : resource)
            .ToArray();
        await store.SaveAsync(current.Graph with { Resources = resources }, current.Etag);
    }

    private static async Task SaveCatalogAsync(WebAppFixture task, MetadataV2Graph graph, string? expectedEtag)
    {
        await using var scope = task.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().SaveAsync(graph, expectedEtag);
    }
}
