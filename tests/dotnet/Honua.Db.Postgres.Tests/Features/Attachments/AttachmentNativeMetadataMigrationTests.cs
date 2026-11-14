// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Attachments;

[Collection("Database")]
public sealed class AttachmentNativeMetadataMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_PreservesKeywordsDigestAndIndependentPhysicalAuthorities()
    {
        await WithSchemaAsync(async schema =>
        {
            await ExecuteAsync(schema, Insert);
            await ExecuteAsync(schema, Insert.Replace("'first.attachments'", "'second.attachments'", StringComparison.Ordinal));
            await ApplyMigrationAsync(schema);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT keywords, content_sha256, global_id FROM "{schema}".attachment_native_metadata
                ORDER BY attachment_authority
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var identifiers = new List<Guid>();
            while (await reader.ReadAsync())
            {
                reader.GetFieldValue<string[]>(0).Should().Equal("a,b", "", " spaced ", "λ");
                reader.GetFieldValue<byte[]>(1).Should().Equal(Enumerable.Repeat((byte)1, 32));
                identifiers.Add(reader.GetGuid(2));
            }
            identifiers.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        });
    }

    [IntegrationTest]
    public async Task Migration_InvalidBindingsAndDigest_AreRejectedAndExactAuthorityIsUnique()
    {
        await WithSchemaAsync(async schema =>
        {
            foreach (var invalid in new[]
            {
                Insert.Replace("repeat('01', 32)", "repeat('01', 31)", StringComparison.Ordinal),
                Insert.Replace("0, 42", "-1, 42", StringComparison.Ordinal),
                Insert.Replace("'blob/path'", "''", StringComparison.Ordinal),
                Insert.Replace("ARRAY['a,b', '', ' spaced ', 'λ']", "ARRAY[NULL]::text[]", StringComparison.Ordinal)
            })
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema, invalid));
                failure.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            }
            await ExecuteAsync(schema, Insert);
            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema, Insert));
            duplicate.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        });
    }

    private const string Insert = """
        INSERT INTO "{schema}".attachment_native_metadata
            (attachment_authority, attachment_id, layer_id, feature_id, storage_path, keywords, content_sha256)
        VALUES ('first.attachments', 7, 0, 42, 'blob/path', ARRAY['a,b', '', ' spaced ', 'λ'],
            decode(repeat('01', 32), 'hex'));
        """;

    private async Task WithSchemaAsync(Func<string, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(AttachmentNativeMetadataMigrationTests));
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
        var path = Path.Join(root!.FullName, "src", "Honua.Server", "Migrations", "163_CreateAttachmentNativeMetadata.sql");
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }
}
