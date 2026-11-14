// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Caching;

public sealed partial class MultiTaskCacheCoherenceTests
{
    private readonly string _externalSchema = "external_" + Guid.NewGuid().ToString("N");

    [IntegrationTest]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task ExternalEditOnTaskA_RetiresTaskBCachedCollectionAndTileBeforePurgeWorkerRuns()
    {
        using var client = _taskB.CreateClient();
        await ReadUntilReplayedAsync(client, CollectionPath);
        await ReadUntilReplayedAsync(client, TilePath);
        await using var command = _taskA.Postgres.DataSource.CreateCommand($"""
            INSERT INTO "{_externalSchema}".external_layer_revisions (layer_id, enabled) VALUES (0, true);
            SELECT "{_externalSchema}".record_external_layer_change(0);
            """);
        await command.ExecuteNonQueryAsync();

        using var collection = await client.GetAsync(CollectionPath);
        using var tile = await client.GetAsync(TilePath);
        collection.EnsureSuccessStatusCode();
        tile.EnsureSuccessStatusCode();
        CacheStatus(collection).Should().StartWith("Honua; fwd=miss");
        CacheStatus(tile).Should().StartWith("Honua; fwd=miss");
        await ReadUntilReplayedAsync(client, CollectionPath);
        await ReadUntilReplayedAsync(client, TilePath);
    }

    private void ConfigureExternalRevisions(IServiceCollection services)
        => services.AddSingleton<IExternalLayerRevisionSource>(provider => new PostgresExternalLayerRevisions(
            () => new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>(), _externalSchema),
            provider.GetRequiredService<IHttpContextAccessor>()));

    private async Task InstallExternalEditTablesAsync()
    {
        var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
            "src", "Honua.Server", "Migrations", "165_CreateExternalLayerChanges.sql"));
        await using var command = _taskA.Postgres.DataSource.CreateCommand(
            $"CREATE SCHEMA \"{_externalSchema}\";" + sql.Replace("$HonuaSchema$", $"\"{_externalSchema}\"", StringComparison.Ordinal));
        await command.ExecuteNonQueryAsync();
    }
}
