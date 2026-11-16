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

        await PinHorizonAsync(published);
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

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadWindow_HorizonBelowNewestChanges_CountsOnlyPublishedChanges()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734303);
        await layer.InsertAsync("""{"name":"first"}""");
        var first = await layer.HeadAsync();
        await layer.InsertAsync("""{"name":"second"}""");
        var published = await layer.HeadAsync();
        await InsertUnpublishedAsync(layer, 3);

        await PinHorizonAsync(published);
        try
        {
            var log = CreateLog(new RealtimeOptions { ResumeMaxEvents = 2 }, TimeProvider.System);
            var window = await log.ReadWindowAsync(CancellationToken.None);
            var cached = await log.ReadWindowAsync(CancellationToken.None);

            window.Head.Should().Be(published);
            window.Oldest.Should().BeLessThanOrEqualTo(first, "the two newest published changes stay resumable");
            cached.Should().Be(window);
        }
        finally
        {
            await ExecuteAsync(MigratedBody);
        }
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task ReadWindow_HorizonBelowNewestChanges_AgesOutOnlyPublishedChanges()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, 734304);
        await layer.InsertAsync("""{"name":"published"}""");
        var published = await layer.HeadAsync();
        await InsertUnpublishedAsync(layer, 2);

        await PinHorizonAsync(published);
        try
        {
            var afterRetention = new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(30));
            var window = await CreateLog(new RealtimeOptions(), afterRetention).ReadWindowAsync(CancellationToken.None);

            window.Head.Should().Be(published);
            window.Oldest.Should().Be(published + 1);
        }
        finally
        {
            await ExecuteAsync(MigratedBody);
        }
    }

    private PostgresChangeTracker CreateTracker() => new(Connections());

    private PostgresFeatureChangeLog CreateLog() => CreateLog(new RealtimeOptions(), TimeProvider.System);

    private PostgresFeatureChangeLog CreateLog(RealtimeOptions options, TimeProvider time)
        => new(new JobStoreDatabase(database.DataSource), new RealtimeCapabilityLimits(Options.Create(options)), time);

    private static async Task InsertUnpublishedAsync(FeatureChangeTestLayer layer, int count)
    {
        for (var index = 0; index < count; index++)
        {
            await layer.InsertAsync("""{"name":"not yet published"}""");
        }
    }

    private Task PinHorizonAsync(long generation)
        => ExecuteAsync($"""
            CREATE OR REPLACE FUNCTION honua.feature_change_read_horizon()
            RETURNS BIGINT LANGUAGE sql STABLE AS $$ SELECT {generation}::BIGINT $$;
            """);

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
