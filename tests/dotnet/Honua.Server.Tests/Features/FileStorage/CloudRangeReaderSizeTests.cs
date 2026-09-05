// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Amazon.S3;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Honua.FileStorage;
using Moq;

namespace Honua.Server.Tests.Features.FileStorage;

public sealed class CloudRangeReaderSizeTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task GetObjectSizeAsync_AwsFailure_NormalizesOnlyNotFound(HttpStatusCode status)
    {
        var failure = new AmazonS3Exception("probe failed") { StatusCode = status };
        var client = new Mock<IAmazonS3>();
        client.Setup(value => value.GetObjectMetadataAsync("bucket", "key", It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var reader = new AwsS3RangeReader(client.Object);

        var exception = await Record.ExceptionAsync(() => reader.GetObjectSizeAsync("bucket", "key"));

        if (status == HttpStatusCode.NotFound)
        {
            exception.Should().BeOfType<FileNotFoundException>().Which.InnerException.Should().BeSameAs(failure);
        }
        else
        {
            exception.Should().BeSameAs(failure);
        }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(500)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task GetObjectSizeAsync_AzureFailure_NormalizesOnlyNotFound(int status)
    {
        var failure = new RequestFailedException(status, "probe failed");
        var blob = new Mock<BlobClient>();
        blob.Setup(value => value.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var container = new Mock<BlobContainerClient>();
        container.Setup(value => value.GetBlobClient("key")).Returns(blob.Object);
        var client = new Mock<BlobServiceClient>();
        client.Setup(value => value.GetBlobContainerClient("bucket")).Returns(container.Object);
        var reader = new AzureBlobRangeReader(client.Object);

        var exception = await Record.ExceptionAsync(() => reader.GetObjectSizeAsync("bucket", "key"));

        if (status == 404)
        {
            exception.Should().BeOfType<FileNotFoundException>().Which.InnerException.Should().BeSameAs(failure);
        }
        else
        {
            exception.Should().BeSameAs(failure);
        }
    }
}
