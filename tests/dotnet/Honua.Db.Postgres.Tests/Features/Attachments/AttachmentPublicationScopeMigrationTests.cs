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
            await InsertPublishedSessionAsync(schema, new SessionRow(2, 42, 7));
            await InsertPublishedSessionAsync(schema, new SessionRow(3, 42, null));
            await InsertPublishedSessionAsync(schema, new SessionRow(4, 0, 0));
            await ApplyMigrationAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"SELECT published_layer_id FROM \"{schema}\".attachment_upload_sessions WHERE feature_id > 1 ORDER BY feature_id", connection);
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
            await using var command = new NpgsqlCommand($"SELECT published_layer_id FROM \"{schema}\".attachment_upload_sessions WHERE feature_id = 1", connection);
            (await command.ExecuteScalarAsync()).Should().Be(DBNull.Value);
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => InsertPublishedSessionAsync(schema, new SessionRow(2, 42, -1)));
            invalid.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        });
    }

    private async Task WithSchemaAsync(Func<string, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentPublicationScopeMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema, "181_CreateAttachmentUploadSessions.sql");
            await InsertLegacySessionAsync(schema, 1, 42);
            await ApplyMigrationAsync(schema);
            await action(schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema, string filename = "196_AddAttachmentPublicationScope.sql")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
        {
            root = root.Parent;
        }
        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", filename);
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private sealed record SessionRow(long FeatureId, int LayerId, int? PublishedLayerId);

    private async Task InsertPublishedSessionAsync(string schema, SessionRow row)
    {
        await InsertLegacySessionAsync(schema, row.FeatureId, row.LayerId);
        var publicationValue = row.PublishedLayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        await ExecuteAsync(schema, $"UPDATE \"{schema}\".attachment_upload_sessions SET published_layer_id = {publicationValue} WHERE feature_id = {row.FeatureId}");
    }

    private async Task InsertLegacySessionAsync(string schema, long featureId, int layerId)
    {
        await ExecuteAsync(schema, $"""
            INSERT INTO "{schema}".attachment_upload_sessions
                (session_id, owner_id, service_id, layer_id, feature_id, operation, idempotency_key,
                 request_sha256, filename, content_type, expected_size, content_sha256, max_chunk_bytes, expires_at)
            VALUES ('{Guid.NewGuid()}', 'owner', 'service', {layerId}, {featureId}, 'chunked', 'key',
                decode(repeat('00', 32), 'hex'), 'file.bin', 'application/octet-stream', 1,
                decode(repeat('00', 32), 'hex'), 1024, now() + interval '1 hour')
            """);
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
