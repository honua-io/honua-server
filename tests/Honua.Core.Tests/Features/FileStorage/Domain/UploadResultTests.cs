// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FileStorage.Domain;

/// <summary>
/// Unit tests for UploadResult domain model
/// </summary>
public class UploadResultTests
{
    [UnitTest]
    public void CreateSuccess_ShouldReturnSuccessResult()
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds(150);
        var cloudFile = CreateTestCloudFile();

        // Act
        var result = UploadResult.CreateSuccess(cloudFile, duration);

        // Assert
        result.Success.Should().BeTrue();
        result.File.Should().Be(cloudFile);
        result.ErrorMessage.Should().BeNull();
        result.Duration.Should().Be(duration);
    }

    [UnitTest]
    public void CreateFailure_ShouldReturnFailureResult()
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds(50);
        const string errorMessage = "File size exceeds maximum allowed";

        // Act
        var result = UploadResult.CreateFailure(errorMessage, duration);

        // Assert
        result.Success.Should().BeFalse();
        result.File.Should().BeNull();
        result.ErrorMessage.Should().Be(errorMessage);
        result.Duration.Should().Be(duration);
    }

    [UnitTest]
    public void CreateSuccess_WithDefaultDuration_ShouldHaveZeroDuration()
    {
        // Arrange
        var cloudFile = CreateTestCloudFile();

        // Act
        var result = UploadResult.CreateSuccess(cloudFile);

        // Assert
        result.Success.Should().BeTrue();
        result.Duration.Should().Be(TimeSpan.Zero);
    }

    private static CloudFile CreateTestCloudFile() => new()
    {
        FileId = "test-123",
        FileName = "test.geojson",
        StoragePath = "test/test-123.geojson",
        ContentType = "application/geo+json",
        SizeBytes = 1024,
        UploadedAt = DateTimeOffset.UtcNow,
        Provider = CloudStorageProvider.Local
    };
}
