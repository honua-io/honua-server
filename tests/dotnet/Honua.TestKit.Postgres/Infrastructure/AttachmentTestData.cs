// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Npgsql;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Helper methods for seeding attachment test data.
/// </summary>
public static class AttachmentTestData
{
    /// <summary>
    /// Exact bytes seeded as <c>test1.txt</c> (attachment id 1). Exposed so download tests
    /// can assert the served bytes rather than only that the response was non-empty
    /// (honua-server#4404).
    /// </summary>
    public static ReadOnlySpan<byte> SeededTextFileBytes => "Test file content"u8;

    /// <summary>
    /// Exact bytes seeded as <c>test2.jpg</c> (attachment id 2) — a minimal JPEG SOI/EOI
    /// marker pair, so a binary body can be byte-compared without a text codepath.
    /// </summary>
    public static ReadOnlySpan<byte> SeededImageFileBytes => [0xFF, 0xD8, 0xFF, 0xD9];

    public static async Task SeedAsync(PostgresFixture fixture, ICloudFileStorage fileStorage, int layerId, long featureId, string schemaName = "honua")
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fileStorage);

        var tableName = QualifyAttachmentTable(schemaName);
        var file1Name = "test1.txt";
        var file2Name = "test2.jpg";
        var file1Bytes = SeededTextFileBytes.ToArray();
        var file2Bytes = SeededImageFileBytes.ToArray();
        var folder = BuildAttachmentFolder(layerId, featureId, schemaName);

        var upload1 = await fileStorage.UploadAsync(new ByteArrayUploadRequest
        {
            Content = file1Bytes,
            FileName = file1Name,
            ContentType = "text/plain",
            Folder = folder
        });

        if (!upload1.Success || upload1.File == null)
        {
            throw new InvalidOperationException("Failed to seed attachment file test1.txt");
        }

        var upload2 = await fileStorage.UploadAsync(new ByteArrayUploadRequest
        {
            Content = file2Bytes,
            FileName = file2Name,
            ContentType = "image/jpeg",
            Folder = folder
        });

        if (!upload2.Success || upload2.File == null)
        {
            await TryDeleteFileAsync(fileStorage, upload1.File.FileId);
            throw new InvalidOperationException("Failed to seed attachment file test2.jpg");
        }

        // #2020: serialize the fixture attachments DELETE + INSERT + setval seed under the
        // schema-mutation advisory lock (multi-statement, parameterized, sequence reset). The
        // file-cleanup compensation stays OUTSIDE the lock action so it runs only after the lock
        // helper has exhausted its bounded 40P01/40001 retries — a transient deadlock retries the
        // seed without prematurely deleting the uploaded blobs.
        try
        {
            await fixture.RunUnderSchemaMutationLockAsync(async () =>
            {
                await using var connection = await fixture.DataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    DELETE FROM {tableName}
                    WHERE layer_id = @layerId AND feature_id = @featureId;

                    INSERT INTO {tableName} (
                        id,
                        feature_id,
                        layer_id,
                        filename,
                        content_type,
                        size,
                        created_at,
                        storage_path,
                        keywords
                    )
                    VALUES
                        (1, @featureId, @layerId, @file1Name, 'text/plain', @file1Size, @file1CreatedAt, @storagePath1, 'test,document'),
                        (2, @featureId, @layerId, @file2Name, 'image/jpeg', @file2Size, @file2CreatedAt, @storagePath2, 'test,image');

                    SELECT setval(
                        pg_get_serial_sequence(@tableName, 'id'),
                        (SELECT COALESCE(MAX(id), 0) FROM {tableName})
                    );
                    """;
                command.Parameters.AddWithValue("tableName", tableName);
                command.Parameters.AddWithValue("layerId", layerId);
                command.Parameters.AddWithValue("featureId", featureId);
                command.Parameters.AddWithValue("file1Name", file1Name);
                command.Parameters.AddWithValue("file1Size", upload1.File.SizeBytes);
                command.Parameters.AddWithValue("file1CreatedAt", upload1.File.UploadedAt.UtcDateTime);
                command.Parameters.AddWithValue("storagePath1", upload1.File.FileId);
                command.Parameters.AddWithValue("file2Name", file2Name);
                command.Parameters.AddWithValue("file2Size", upload2.File.SizeBytes);
                command.Parameters.AddWithValue("file2CreatedAt", upload2.File.UploadedAt.UtcDateTime);
                command.Parameters.AddWithValue("storagePath2", upload2.File.FileId);

                await command.ExecuteNonQueryAsync();
            });
        }
        catch
        {
            await TryDeleteFileAsync(fileStorage, upload1.File.FileId);
            await TryDeleteFileAsync(fileStorage, upload2.File.FileId);
            throw;
        }
    }

    public static async Task CleanupAsync(PostgresFixture fixture, ICloudFileStorage fileStorage, int layerId, long featureId, string schemaName = "honua")
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fileStorage);

        var tableName = QualifyAttachmentTable(schemaName);
        var fileIds = new List<string>();

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT storage_path
                FROM {tableName}
                WHERE layer_id = @layerId AND feature_id = @featureId;
                """;
            command.Parameters.AddWithValue("layerId", layerId);
            command.Parameters.AddWithValue("featureId", featureId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                {
                    fileIds.Add(reader.GetString(0));
                }
            }
        }

        // #2020: route the fixture attachments DELETE through the schema-mutation advisory
        // lock (parameterized overload). The preceding SELECT is read-only and stays unlocked.
        await fixture.ApplyGlobalSeedSqlAsync(
            $"""
            DELETE FROM {tableName}
            WHERE layer_id = @layerId AND feature_id = @featureId;
            """,
            command =>
            {
                command.Parameters.AddWithValue("layerId", layerId);
                command.Parameters.AddWithValue("featureId", featureId);
            });

        foreach (var fileId in fileIds)
        {
            await TryDeleteFileAsync(fileStorage, fileId);
        }
    }

    private static string BuildAttachmentFolder(int layerId, long featureId, string schemaName)
    {
        return string.Join(
            '/',
            "attachments",
            schemaName,
            layerId.ToString(CultureInfo.InvariantCulture),
            featureId.ToString(CultureInfo.InvariantCulture));
    }

    private static string QualifyAttachmentTable(string schemaName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        using var builder = new NpgsqlCommandBuilder();
        return $"{builder.QuoteIdentifier(schemaName)}.attachments";
    }

    private static async Task TryDeleteFileAsync(ICloudFileStorage fileStorage, string fileId)
    {
        try
        {
            await fileStorage.DeleteAsync(fileId);
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Broad catch is intentional: best-effort cleanup for test data, any failure
            // here (already deleted, storage unavailable, etc.) should not fail the test.
        }
    }
}
