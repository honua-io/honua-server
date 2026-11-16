// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Infrastructure.Abstractions;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Realtime;

// Change-feed readers take their committed horizon from honua.feature_change_read_horizon(). A
// later release replaces only that function body, so these tests replace it in their own migrated
// database (never the shared one) and check that the running readers follow it.
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class FeatureChangeReadHorizonTests(FeatureChangeDatabaseFixture database)
    : IClassFixture<FeatureChangeDatabaseFixture>
{
    private const string MigratedBody = """
        CREATE OR REPLACE FUNCTION honua.feature_change_read_horizon()
        RETURNS BIGINT LANGUAGE sql STABLE
        AS $$ SELECT COALESCE(MAX(generation), 0)::BIGINT FROM honua.feature_changes $$;
        """;

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadHorizon_AsMigrated_IsTheCommittedMaximum()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734301);
        await layer.InsertAsync("""{"name":"horizon"}""");
        var head = await layer.HeadAsync();

        (await ReadHorizonAsync()).Should().Be(head);
        (await CreateTracker().GetCurrentGenerationAsync()).Should().Be(head);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadHorizon_ReplacedBody_IsWhatTrackerAndVersionCreationUse()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734302);
        var before = await layer.HeadAsync();
        await layer.InsertAsync("""{"name":"published"}""");
        var published = await layer.HeadAsync();
        await layer.InsertAsync("""{"name":"not yet published"}""");
        (await layer.HeadAsync()).Should().BeGreaterThan(published);

        await ExecuteAsync($"""
            CREATE OR REPLACE FUNCTION honua.feature_change_read_horizon()
            RETURNS BIGINT LANGUAGE sql STABLE AS $$ SELECT {published}::BIGINT $$;
            """);
        try
        {
            (await CreateTracker().GetCurrentGenerationAsync()).Should().Be(published);

            var version = await new PostgresVersionManager(Connections()).CreateAsync(
                new CreateVersionRequest($"horizon_{Guid.NewGuid():N}", "sde", VersionAccess.Public));
            version.CommonAncestorGeneration.Should().Be(published);
            version.BranchGeneration.Should().Be(published);

            var log = CreateLog();
            (await log.ReadHeadAsync(CancellationToken.None)).Should().Be(published);
            var changes = await log.ReadAsync(
                new FeatureChangeLogQuery(before, null, null, [layer.LayerId], 10), CancellationToken.None);
            changes.Select(change => change.Cursor).Should().Equal(published);
        }
        finally
        {
            await ExecuteAsync(MigratedBody);
        }
    }

    private PostgresChangeTracker CreateTracker() => new(Connections());

    private PostgresFeatureChangeLog CreateLog()
        => new(
            new JobStoreDatabase(database.DataSource),
            new RealtimeCapabilityLimits(Options.Create(new RealtimeOptions())),
            TimeProvider.System);

    private TestDatabaseConnectionProvider Connections() => new(database.DataSource);

    private async Task<long> ReadHorizonAsync()
    {
        await using var command = database.DataSource.CreateCommand("SELECT honua.feature_change_read_horizon()");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
