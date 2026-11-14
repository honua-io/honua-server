// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

[Collection("Database")]
public sealed class AttachmentUploadSessionMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_PreservesSessionChunksAndCommittedResult()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentUploadSessionMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            await ExecuteAsync(schema, """
                INSERT INTO "{schema}".attachment_upload_sessions
                    (session_id, owner_id, service_id, layer_id, feature_id, operation,
                     idempotency_key, request_sha256, filename, content_type, expected_size,
                     content_sha256, max_chunk_bytes, expires_at)
                VALUES ('62b531d9-5116-4d00-a49b-d49d5fcc01f1', 'owner', 'service', 0, 42,
                    'chunked', 'retry', decode(repeat('01', 32), 'hex'), 'photo.png', 'image/png',
                    3, decode(repeat('02', 32), 'hex'), 1048576, now() + interval '1 hour');
                INSERT INTO "{schema}".attachment_upload_chunks
                    (session_id, offset_bytes, content, content_sha256)
                VALUES ('62b531d9-5116-4d00-a49b-d49d5fcc01f1', 0, decode('616263', 'hex'),
                    decode(repeat('03', 32), 'hex'));
                UPDATE "{schema}".attachment_upload_sessions
                SET next_offset_bytes = 3, state = 'committed',
                    committed_attachment = '{"attachmentId":"42","name":"photo.png"}'::jsonb
                WHERE idempotency_key = 'retry';
                """);
            await ApplyMigrationAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT s.next_offset_bytes, s.state, s.committed_attachment->>'attachmentId', c.content
                FROM "{schema}".attachment_upload_sessions s
                JOIN "{schema}".attachment_upload_chunks c USING (session_id)
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(3);
            reader.GetString(1).Should().Be("committed");
            reader.GetString(2).Should().Be("42");
            reader.GetFieldValue<byte[]>(3).Should().Equal("abc"u8.ToArray());
            (await reader.ReadAsync()).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Migration_IdempotencyKeys_AreUniqueWithinCallerTargetAndOperation()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentUploadSessionMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            const string insert = """
                INSERT INTO "{schema}".attachment_mutation_receipts
                    (owner_id, service_id, layer_id, feature_id, operation, idempotency_key,
                     request_sha256, attachment_id, expires_at)
                VALUES ('owner', 'service', 0, 42, 'delete', 'retry',
                    decode(repeat('01', 32), 'hex'), 7, now() + interval '1 hour')
                """;
            await ExecuteAsync(schema, insert);
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema, insert));
            duplicate.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            await ExecuteAsync(schema, insert.Replace("'owner'", "'other-owner'", StringComparison.Ordinal));
            await ExecuteAsync(schema, insert.Replace("0, 42", "0, 43", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Migration_InvalidSessionAndChunkState_IsRejected()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentUploadSessionMigrationTests));
        try
        {
            await ApplyMigrationAsync(schema);
            const string insert = """
                INSERT INTO "{schema}".attachment_upload_sessions
                    (session_id, owner_id, service_id, layer_id, feature_id, operation,
                     idempotency_key, request_sha256, filename, content_type, expected_size,
                     content_sha256, max_chunk_bytes, expires_at)
                VALUES (gen_random_uuid(), 'owner', 'service', 0, 0, 'unary', 'retry',
                    decode(repeat('01', 32), 'hex'), 'empty.txt', 'text/plain', 0,
                    decode(repeat('02', 32), 'hex'), 1048576, now() + interval '1 hour')
                """;
            foreach (var invalid in new[]
            {
                insert.Replace("'text/plain', 0", "'text/plain', -1", StringComparison.Ordinal),
                insert.Replace("repeat('02', 32)", "repeat('02', 31)", StringComparison.Ordinal),
                insert.Replace("1048576", "0", StringComparison.Ordinal)
            })
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema, invalid));
                failure.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            }
            await ExecuteAsync(schema, insert);
            var overflow = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema,
                "UPDATE \"{schema}\".attachment_upload_sessions SET next_offset_bytes = 1"));
            overflow.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            var missingResult = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema,
                "UPDATE \"{schema}\".attachment_upload_sessions SET state = 'committed'"));
            missingResult.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
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
        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", "162_CreateAttachmentUploadSessions.sql");
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
