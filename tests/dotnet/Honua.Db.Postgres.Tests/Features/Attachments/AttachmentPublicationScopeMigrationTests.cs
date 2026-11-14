// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

[Collection("Database")]
public sealed class AttachmentPublicationScopeMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_PreservesDistinctPublishedAndPhysicalLayers()
    {
        await WithSchemaAsync(async schema =>
        {
            await ExecuteAsync(schema, "INSERT INTO \"{schema}\".attachment_upload_sessions (layer_id, published_layer_id) VALUES (42, 7), (42, NULL), (0, 0)");
            await ApplyMigrationAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"SELECT published_layer_id FROM \"{schema}\".attachment_upload_sessions WHERE row_id > 1 ORDER BY row_id", connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(7);
            (await reader.ReadAsync()).Should().BeTrue();
            reader.IsDBNull(0).Should().BeTrue();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(0);
        });
    }

    [IntegrationTest]
    public async Task Migration_ExistingRows_KeepUnknownPublicationAndRejectNegativeIds()
    {
        await WithSchemaAsync(async schema =>
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"SELECT published_layer_id FROM \"{schema}\".attachment_upload_sessions WHERE row_id = 1", connection);
            (await command.ExecuteScalarAsync()).Should().Be(DBNull.Value);
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema,
                "INSERT INTO \"{schema}\".attachment_upload_sessions (layer_id, published_layer_id) VALUES (42, -1)"));
            invalid.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        });
    }

    private async Task WithSchemaAsync(Func<string, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentPublicationScopeMigrationTests));
        try
        {
            await ExecuteAsync(schema, "CREATE TABLE \"{schema}\".attachment_upload_sessions (row_id SERIAL PRIMARY KEY, layer_id INTEGER NOT NULL); INSERT INTO \"{schema}\".attachment_upload_sessions (layer_id) VALUES (42)");
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
        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", "165_AddAttachmentPublicationScope.sql");
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
