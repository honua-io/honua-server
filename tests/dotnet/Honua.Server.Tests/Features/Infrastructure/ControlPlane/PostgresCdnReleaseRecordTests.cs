// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Collection("Database")]
public sealed class PostgresCdnReleaseRecordTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await ExecuteAsync("CREATE SCHEMA IF NOT EXISTS honua");
        await ExecuteAsync(await File.ReadAllTextAsync(MigrationPath()));
        await ExecuteAsync("DELETE FROM honua.cdn_release_record");
    }

    public Task DisposeAsync() => _postgres.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TryRecordChangeAsync_TasksOfOneDeploymentStartTogether_ExactlyOneAnnouncesTheRelease()
    {
        var record = new PostgresCdnReleaseRecord(_postgres.DataSource);

        var announcements = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => record.TryRecordChangeAsync("sha256:new", CancellationToken.None)));

        announcements.Count(announced => announced).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TryRecordChangeAsync_SameReleaseRestarts_AnnouncesNothingUntilTheReleaseChanges()
    {
        var record = new PostgresCdnReleaseRecord(_postgres.DataSource);

        var first = await record.TryRecordChangeAsync("sha256:a", CancellationToken.None);
        var restart = await record.TryRecordChangeAsync("sha256:a", CancellationToken.None);
        var nextRelease = await record.TryRecordChangeAsync("sha256:b", CancellationToken.None);
        var rollback = await record.TryRecordChangeAsync("sha256:a", CancellationToken.None);

        (first, restart, nextRelease, rollback).Should().Be((true, false, true, true));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _postgres.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static string MigrationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Join(directory!.FullName, "src", "Honua.Server", "Migrations", "151_CreateCdnReleaseRecord.sql");
    }
}
