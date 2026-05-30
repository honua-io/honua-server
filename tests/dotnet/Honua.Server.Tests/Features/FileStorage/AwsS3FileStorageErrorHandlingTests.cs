// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

[Collection("Unit")]
public sealed class AwsS3FileStorageErrorHandlingTests
{
    [UnitTest]
    public async Task UploadAsync_WithInvalidConfiguredPrefix_ReturnsGenericFailureMessage()
    {
        var storage = CreateStorage(keyPrefix: "/invalid-prefix");

        var result = await storage.UploadAsync(new ByteArrayUploadRequest
        {
            Content = "prefix failure"u8.ToArray(),
            FileName = "prefix.txt",
            ContentType = "text/plain"
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("File upload failed.");
    }

    [UnitTest]
    public async Task UploadAsync_WithInvalidFolderInput_ReturnsFolderValidationMessage()
    {
        var storage = CreateStorage(keyPrefix: null);
        using var content = new MemoryStream("folder failure"u8.ToArray());

        var result = await storage.UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = "folder.txt",
            ContentType = "text/plain",
            Folder = "../escape"
        });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Folder");
    }

    private static AwsS3FileStorage CreateStorage(string? keyPrefix)
    {
        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "unit-test-bucket",
                Region = "us-east-1",
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                KeyPrefix = keyPrefix
            }
        });

        return new AwsS3FileStorage(
            options,
            NullLogger<AwsS3FileStorage>.Instance,
            new InMemoryUploadProgressStore());
    }
}
