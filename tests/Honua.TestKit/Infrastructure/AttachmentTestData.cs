// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Helper methods for seeding attachment test data.
/// </summary>
public static class AttachmentTestData
{
    public static async Task SeedAsync(PostgresFixture fixture, int layerId, long featureId, string storageBasePath)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(storageBasePath);

        var layerFolder = layerId.ToString(CultureInfo.InvariantCulture);
        var featureFolder = featureId.ToString(CultureInfo.InvariantCulture);
        var attachmentDirectory = Path.Combine(storageBasePath, layerFolder, featureFolder);
        Directory.CreateDirectory(attachmentDirectory);

        var file1Name = "test1.txt";
        var file2Name = "test2.jpg";
        var file1Bytes = Encoding.UTF8.GetBytes("Test file content");
        var file2Bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };

        var file1Path = Path.Combine(attachmentDirectory, file1Name);
        var file2Path = Path.Combine(attachmentDirectory, file2Name);
        await File.WriteAllBytesAsync(file1Path, file1Bytes);
        await File.WriteAllBytesAsync(file2Path, file2Bytes);

        var storagePath1 = Path.Combine(layerFolder, featureFolder, file1Name).Replace("\\", "/", StringComparison.Ordinal);
        var storagePath2 = Path.Combine(layerFolder, featureFolder, file2Name).Replace("\\", "/", StringComparison.Ordinal);

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
                (1, @featureId, @layerId, @file1Name, 'text/plain', @file1Size, NOW(), @storagePath1, 'test,document'),
                (2, @featureId, @layerId, @file2Name, 'image/jpeg', @file2Size, NOW(), @storagePath2, 'test,image');

            SELECT setval(
                pg_get_serial_sequence('honua.attachments', 'id'),
                (SELECT COALESCE(MAX(id), 0) FROM honua.attachments)
            );
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("featureId", featureId);
        command.Parameters.AddWithValue("file1Name", file1Name);
        command.Parameters.AddWithValue("file1Size", file1Bytes.Length);
        command.Parameters.AddWithValue("storagePath1", storagePath1);
        command.Parameters.AddWithValue("file2Name", file2Name);
        command.Parameters.AddWithValue("file2Size", file2Bytes.Length);
        command.Parameters.AddWithValue("storagePath2", storagePath2);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task CleanupAsync(PostgresFixture fixture, int layerId, long featureId, string storageBasePath)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(storageBasePath);

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

        var attachmentDirectory = Path.Combine(
            storageBasePath,
            layerId.ToString(CultureInfo.InvariantCulture),
            featureId.ToString(CultureInfo.InvariantCulture));

        if (Directory.Exists(attachmentDirectory))
        {
            Directory.Delete(attachmentDirectory, recursive: true);
        }
    }
}
