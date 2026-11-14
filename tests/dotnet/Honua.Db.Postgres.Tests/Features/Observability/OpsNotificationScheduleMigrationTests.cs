// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Observability;

[Collection("Database")]
public sealed class OpsNotificationScheduleMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_RepeatedApplication_PreservesOneScheduleAcrossConnections()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(OpsNotificationScheduleMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            {
                await using var command = new NpgsqlCommand($"""
                    INSERT INTO "{schema}".ops_notification_schedules
                        (source, subject_id, episode_id, first_seen_at, severity, notice_number,
                         next_notice_at, last_seen_at)
                    VALUES ('finding', 'stable-finding', '0290b930-4356-400d-a5ce-e449b123622b',
                        '2026-11-14T00:00:00Z', 2, 1, '2026-11-14T00:30:00Z', '2026-11-14T00:00:00Z')
                    ON CONFLICT (source, subject_id) DO NOTHING
                    """, connection);
                (await command.ExecuteNonQueryAsync()).Should().Be(1);
                (await command.ExecuteNonQueryAsync()).Should().Be(0);
            }

            await ApplyMigrationAsync(schema);
            await using var reopened = await fixture.DataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand($"""
                SELECT count(*), min(next_notice_at), min(notice_number)
                FROM "{schema}".ops_notification_schedules
                """, reopened);
            await using var reader = await read.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(1);
            reader.GetFieldValue<DateTimeOffset>(1).Should().Be(new DateTimeOffset(2026, 11, 14, 0, 30, 0, TimeSpan.Zero));
            reader.GetInt32(2).Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Migration_InvalidSeverityOrNegativeNoticeNumber_IsRejected()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(OpsNotificationScheduleMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            foreach (var (severity, noticeNumber) in new[] { (-1, 0), (3, 0), (1, -1) })
            {
                await using var command = new NpgsqlCommand($"""
                    INSERT INTO "{schema}".ops_notification_schedules
                        (source, subject_id, episode_id, first_seen_at, severity,
                         notice_number, next_notice_at, last_seen_at)
                    VALUES ('finding', 'invalid', gen_random_uuid(), now(), @severity, @number, now(), now())
                    """, connection);
                command.Parameters.AddWithValue("severity", severity);
                command.Parameters.AddWithValue("number", noticeNumber);
                var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                exception.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            }
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
        {
            root = root.Parent;
        }

        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", "161_CreateOpsNotificationSchedules.sql");
        var sql = (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
