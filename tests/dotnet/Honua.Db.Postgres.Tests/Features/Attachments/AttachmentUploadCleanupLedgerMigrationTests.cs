// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

[Collection("Database")]
public sealed class AttachmentUploadCleanupLedgerMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_RetainsCleanupIntentWithoutAnUploadSession()
    {
        await WithSchemaAsync(async schema =>
        {
            await ExecuteAsync(schema, """
                INSERT INTO "{schema}".attachment_upload_cleanup_ledger (session_id, layer_id, feature_id)
                VALUES ('01234567-89ab-cdef-0123-456789abcdef', 0, 42);
                """).ConfigureAwait(false);
            await ApplyMigrationAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT count(*) FROM "{schema}".attachment_upload_cleanup_ledger
                WHERE next_attempt_at <= now()
                """, connection);
            (await command.ExecuteScalarAsync()).Should().Be(1L);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema, """
                INSERT INTO "{schema}".attachment_upload_cleanup_ledger (session_id, layer_id, feature_id)
                VALUES (gen_random_uuid(), -1, 42);
                """));
            failure.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        });
    }

    private async Task WithSchemaAsync(Func<string, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentUploadCleanupLedgerMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            await action(schema);
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
        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", "164_CreateAttachmentUploadCleanupLedger.sql");
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
