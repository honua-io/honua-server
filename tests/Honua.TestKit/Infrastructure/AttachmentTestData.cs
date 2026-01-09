// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Helper methods for seeding attachment test data.
/// </summary>
public static class AttachmentTestData
{
    public static async Task SeedAsync(PostgresFixture fixture, ICloudFileStorage fileStorage, int layerId, long featureId)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fileStorage);

        var file1Name = "test1.txt";
        var file2Name = "test2.jpg";
        var file1Bytes = Encoding.UTF8.GetBytes("Test file content");
        var file2Bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var folder = BuildAttachmentFolder(layerId, featureId);

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

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM honua.attachments
            WHERE layer_id = @layerId AND feature_id = @featureId;

            INSERT INTO honua.attachments (
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
                pg_get_serial_sequence('honua.attachments', 'id'),
                (SELECT COALESCE(MAX(id), 0) FROM honua.attachments)
            );
            """;
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

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            await TryDeleteFileAsync(fileStorage, upload1.File.FileId);
            await TryDeleteFileAsync(fileStorage, upload2.File.FileId);
            throw;
        }
    }

    public static async Task CleanupAsync(PostgresFixture fixture, ICloudFileStorage fileStorage, int layerId, long featureId)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fileStorage);

        var fileIds = new List<string>();

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT storage_path
                FROM honua.attachments
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

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM honua.attachments
                WHERE layer_id = @layerId AND feature_id = @featureId;
                """;
            command.Parameters.AddWithValue("layerId", layerId);
            command.Parameters.AddWithValue("featureId", featureId);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var fileId in fileIds)
        {
            await TryDeleteFileAsync(fileStorage, fileId);
        }
    }

    private static string BuildAttachmentFolder(int layerId, long featureId)
    {
        return string.Join(
            '/',
            "attachments",
            layerId.ToString(CultureInfo.InvariantCulture),
            featureId.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task TryDeleteFileAsync(ICloudFileStorage fileStorage, string fileId)
    {
        try
        {
            await fileStorage.DeleteAsync(fileId);
        }
        catch
        {
            // Best effort cleanup for test data.
        }
    }
}
