// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

[Collection("Database")]
public sealed class AttachmentCleanupRetryMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_RetainsIndependentRetrySchedules()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentCleanupRetryMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            await ExecuteAsync(schema, """
                INSERT INTO "{schema}".attachment_cleanup_retries
                    (cleanup_authority, storage_path, next_attempt_at)
                VALUES ('first.attachments', 'blob/path', now() - interval '1 minute'),
                       ('second.attachments', 'blob/path', now() + interval '1 hour');
                """);
            await ApplyMigrationAsync(schema);
            await AssertSchedulesAsync(schema);
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema, """
                INSERT INTO "{schema}".attachment_cleanup_retries
                    (cleanup_authority, storage_path, next_attempt_at)
                VALUES ('first.attachments', 'blob/path', now());
                """));
            duplicate.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task AssertSchedulesAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*), count(*) FILTER (WHERE next_attempt_at <= now())
            FROM "{schema}".attachment_cleanup_retries
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(2);
        reader.GetInt64(1).Should().Be(1);
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
        {
            root = root.Parent;
        }
        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", "197_CreateAttachmentCleanupRetries.sql");
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
