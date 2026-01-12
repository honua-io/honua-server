// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FileStorage.Domain;

/// <summary>
/// Unit tests for CloudFile domain model
/// </summary>
public class CloudFileTests
{
    [UnitTest]
    public void CloudFile_Create_WithAllProperties_ShouldSetAllValues()
    {
        // Arrange
        var uploadedAt = DateTimeOffset.UtcNow;
        var expiresAt = uploadedAt.AddHours(24);
        var metadata = ImmutableDictionary<string, string>.Empty
            .Add("source", "test")
            .Add("format", "geojson");

        // Act
        var cloudFile = new CloudFile
        {
            FileId = "test-file-123",
            FileName = "test.geojson",
            StoragePath = "uploads/test-file-123.geojson",
            ContentType = "application/geo+json",
            SizeBytes = 1024,
            UploadedAt = uploadedAt,
            ExpiresAt = expiresAt,
            ContentHash = "ABC123",
            Metadata = metadata,
            Provider = CloudStorageProvider.Local
        };

        // Assert
        cloudFile.FileId.Should().Be("test-file-123");
        cloudFile.FileName.Should().Be("test.geojson");
        cloudFile.StoragePath.Should().Be("uploads/test-file-123.geojson");
        cloudFile.ContentType.Should().Be("application/geo+json");
        cloudFile.SizeBytes.Should().Be(1024);
        cloudFile.UploadedAt.Should().Be(uploadedAt);
        cloudFile.ExpiresAt.Should().Be(expiresAt);
        cloudFile.ContentHash.Should().Be("ABC123");
        cloudFile.Metadata.Should().HaveCount(2);
        cloudFile.Provider.Should().Be(CloudStorageProvider.Local);
    }

    [UnitTest]
    public void CloudFile_Create_WithOptionalProperties_ShouldUseDefaults()
    {
        // Act
        var cloudFile = new CloudFile
        {
            FileId = "test-file-456",
            FileName = "test.shp",
            StoragePath = "shapefiles/test-file-456.shp",
            ContentType = "application/octet-stream",
            SizeBytes = 2048,
            UploadedAt = DateTimeOffset.UtcNow,
            Provider = CloudStorageProvider.AwsS3
        };

        // Assert
        cloudFile.ExpiresAt.Should().BeNull();
        cloudFile.ContentHash.Should().BeNull();
        cloudFile.Metadata.Should().BeEmpty();
    }

    [UnitTest]
    public void CloudFile_Equality_WithSameValues_ShouldBeEqual()
    {
        // Arrange
        var uploadedAt = DateTimeOffset.UtcNow;
        var metadata = ImmutableDictionary<string, string>.Empty.Add("key", "value");

        var file1 = new CloudFile
        {
            FileId = "same-id",
            FileName = "file.json",
            StoragePath = "path/file.json",
            ContentType = "application/json",
            SizeBytes = 100,
            UploadedAt = uploadedAt,
            Metadata = metadata,
            Provider = CloudStorageProvider.Local
        };

        var file2 = new CloudFile
        {
            FileId = "same-id",
            FileName = "file.json",
            StoragePath = "path/file.json",
            ContentType = "application/json",
            SizeBytes = 100,
            UploadedAt = uploadedAt,
            Metadata = metadata,
            Provider = CloudStorageProvider.Local
        };

        // Act & Assert
        file1.Should().Be(file2);
        (file1 == file2).Should().BeTrue();
    }
}
