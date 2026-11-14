// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Caching;

[Collection("Database")]
public sealed class ExternalLayerChangeWorkerTests(DatabaseFixtureAdapter fixture)
{
    [IntegrationTest]
    public Task PurgeFails_ChangeRemainsQueuedUntilAnotherHostSuccessfullyRetries()
        => InSchemaAsync(async database =>
        {
            var cdn = Substitute.For<ICdnInvalidator>();
            cdn.InvalidateAsync(default!, default).ReturnsForAnyArgs(
                new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "retry"),
                new CdnInvalidationResult(CdnInvalidationOutcome.Submitted));
            await using var services = new ServiceCollection().BuildServiceProvider();
            var invalidation = new OutputCacheInvalidationService(null, null, null,
                services.GetRequiredService<IServiceScopeFactory>(), null,
                NullLogger<OutputCacheInvalidationService>.Instance,
                new CdnInvalidationService(cdn, NullLogger<CdnInvalidationService>.Instance));
            ExternalLayerChangeWorker Host() => new(database, invalidation,
                NullLogger<ExternalLayerChangeWorker>.Instance);
            using var first = Host();
            var failed = () => first.ProcessOneAsync(default);
            await failed.Should().ThrowAsync<InvalidOperationException>();
            (await CountAsync(database)).Should().Be(1L);
            using var second = Host();
            (await second.ProcessOneAsync(default)).Should().BeTrue();
            (await CountAsync(database)).Should().Be(0L);
            (await second.ProcessOneAsync(default)).Should().BeFalse();
            await cdn.Received(2).InvalidateAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        });

    [IntegrationTest]
    public Task RevisionRead_IsPinnedWithinRequestAndRefreshesOnTheNextRequest()
        => InSchemaAsync(async database =>
        {
            var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            var source = new PostgresExternalLayerRevisions(() => database, accessor);
            var before = await source.GetRevisionAsync();
            await RecordAsync(database);
            (await source.GetRevisionAsync()).Should().Be(before);
            accessor.HttpContext = new DefaultHttpContext();
            (await source.GetRevisionAsync()).Should().NotBe(before);
            accessor.HttpContext = null;
        });

    [IntegrationTest]
    public Task RevisionRead_TooManyTrackedLayers_RefusesPartialGeneration()
        => InSchemaAsync(async database =>
        {
            await database.ExecuteAsync(async (connection, token) =>
            {
                await using var command = new NpgsqlCommand($"""
                    INSERT INTO {database.Table("external_layer_revisions")} (layer_id)
                    SELECT generate_series(8, 1031)
                    """, connection);
                await command.ExecuteNonQueryAsync(token);
            }, StatementRepeat.SafeToRepeat, default);
            var source = new PostgresExternalLayerRevisions(() => database, new HttpContextAccessor());
            var read = () => source.GetRevisionAsync();
            await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1024*");
        });

    private async Task InSchemaAsync(Func<JobStoreDatabase, Task> test)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ExternalLayerChangeWorkerTests));
        try
        {
            var sql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "src", "Honua.Server", "Migrations", "165_CreateExternalLayerChanges.sql"));
            await using var command = fixture.DataSource.CreateCommand(
                sql.Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal)
                + $"INSERT INTO \"{schema}\".external_layer_revisions (layer_id, enabled) VALUES (7, true);");
            await command.ExecuteNonQueryAsync();
            var database = new JobStoreDatabase(fixture.DataSource, schema);
            await RecordAsync(database);
            await test(database);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static Task RecordAsync(JobStoreDatabase database)
        => database.ExecuteAsync(async (connection, token) =>
        {
            await using var command = new NpgsqlCommand($"SELECT {database.Table("record_external_layer_change")}(7)", connection);
            await command.ExecuteNonQueryAsync(token);
        }, StatementRepeat.NotAfterSend, default);

    private static Task<long> CountAsync(JobStoreDatabase database)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {database.Table("external_layer_changes")}", connection);
            return (long)(await command.ExecuteScalarAsync(token))!;
        }, StatementRepeat.SafeToRepeat, default);
}
