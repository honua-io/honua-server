// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FileStorage.Domain;

/// <summary>
/// Unit tests for BatchUploadResult domain model
/// </summary>
public class BatchUploadResultTests
{
    [UnitTest]
    public void CreateSuccess_ShouldReturnSuccessfulBatchResult()
    {
        // Arrange
        var batchId = "batch-123";
        var files = new[]
        {
            CreateTestCloudFile("file1.shp"),
            CreateTestCloudFile("file1.dbf"),
            CreateTestCloudFile("file1.shx")
        };
        var duration = TimeSpan.FromSeconds(2);

        // Act
        var result = BatchUploadResult.CreateSuccess(batchId, files, duration);

        // Assert
        result.Success.Should().BeTrue();
        result.BatchId.Should().Be(batchId);
        result.UploadedFiles.Should().HaveCount(3);
        result.FailedFiles.Should().BeEmpty();
        result.TotalFiles.Should().Be(3);
        result.SuccessCount.Should().Be(3);
        result.FailureCount.Should().Be(0);
        result.Duration.Should().Be(duration);
    }

    [UnitTest]
    public void CreatePartialSuccess_ShouldReturnPartialResult()
    {
        // Arrange
        var batchId = "batch-456";
        var successfulFiles = new[]
        {
            CreateTestCloudFile("file1.shp"),
            CreateTestCloudFile("file1.dbf")
        };
        var failedFiles = new Dictionary<string, string>
        {
            ["file1.prj"] = "Permission denied"
        };
        var duration = TimeSpan.FromSeconds(1);

        // Act
        var result = BatchUploadResult.CreatePartialSuccess(batchId, successfulFiles, failedFiles, duration);

        // Assert
        result.Success.Should().BeFalse();
        result.BatchId.Should().Be(batchId);
        result.UploadedFiles.Should().HaveCount(2);
        result.FailedFiles.Should().HaveCount(1);
        result.FailedFiles["file1.prj"].Should().Be("Permission denied");
        result.TotalFiles.Should().Be(3);
        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(1);
        result.Duration.Should().Be(duration);
    }

    [UnitTest]
    public void CreateFailure_ShouldReturnFailedResult()
    {
        // Arrange
        var batchId = "batch-789";
        var failedFiles = new Dictionary<string, string>
        {
            ["file1.shp"] = "Disk full",
            ["file1.dbf"] = "Disk full"
        };
        var duration = TimeSpan.FromMilliseconds(500);

        // Act
        var result = BatchUploadResult.CreateFailure(batchId, failedFiles, duration);

        // Assert
        result.Success.Should().BeFalse();
        result.BatchId.Should().Be(batchId);
        result.UploadedFiles.Should().BeEmpty();
        result.FailedFiles.Should().HaveCount(2);
        result.TotalFiles.Should().Be(2);
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(2);
        result.Duration.Should().Be(duration);
    }

    private static CloudFile CreateTestCloudFile(string fileName) => new()
    {
        FileId = Guid.NewGuid().ToString("N"),
        FileName = fileName,
        StoragePath = $"batch/{fileName}",
        ContentType = "application/octet-stream",
        SizeBytes = 1024,
        UploadedAt = DateTimeOffset.UtcNow,
        Provider = CloudStorageProvider.Local
    };
}
