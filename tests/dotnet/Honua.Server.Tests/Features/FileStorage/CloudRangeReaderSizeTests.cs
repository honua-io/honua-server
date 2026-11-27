// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    [Theory]
    [InlineData(404)]
    [InlineData(412)]
    [InlineData(500)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ReadRangeAsync_AzureFailure_NormalizesNotFoundAndPreconditionFailed(int status)
    {
        var failure = new RequestFailedException(status, "range read failed");
        var blob = new Mock<BlobClient>();
        blob.Setup(value => value.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var container = new Mock<BlobContainerClient>();
        container.Setup(value => value.GetBlobClient("key")).Returns(blob.Object);
        var client = new Mock<BlobServiceClient>();
        client.Setup(value => value.GetBlobContainerClient("bucket")).Returns(container.Object);
        var reader = new AzureBlobRangeReader(client.Object);

        var exception = await Record.ExceptionAsync(() => reader.ReadRangeAsync("bucket", "key", 0, 16));

        if (status == 404)
        {
            exception.Should().BeOfType<FileNotFoundException>().Which.InnerException.Should().BeSameAs(failure);
        }
        else if (status == 412)
        {
            exception.Should().BeOfType<Honua.Core.Features.Infrastructure.Domain.CloudFileRevisionChangedException>();
        }
        else
        {
            exception.Should().BeSameAs(failure);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ReadRangeAsync_AwsSmallPayloadWithLargeCeiling_ReturnsActualBytes()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        using var stream = new MemoryStream(payload);
        using var response = new Amazon.S3.Model.GetObjectResponse
        {
            ResponseStream = stream,
        };
        response.Headers.ContentLength = payload.Length;
        var client = new Mock<IAmazonS3>();
        client.Setup(value => value.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        var reader = new AwsS3RangeReader(client.Object);

        var bytes = await reader.ReadRangeAsync("bucket", "chunk", 0, 64 * 1024 * 1024);

        bytes.Should().Equal(payload);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task ReadRangeAsync_AzureSmallPayloadWithLargeCeiling_ReturnsActualBytes()
    {
        var payload = new byte[] { 9, 8, 7, 6 };
        using var stream = new MemoryStream(payload);
        var download = BlobsModelFactory.BlobDownloadStreamingResult(
            content: stream,
            details: BlobsModelFactory.BlobDownloadDetails(contentLength: payload.Length));
        var blob = new Mock<BlobClient>();
        blob.Setup(value => value.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(download, Mock.Of<Response>()));
        var container = new Mock<BlobContainerClient>();
        container.Setup(value => value.GetBlobClient("chunk")).Returns(blob.Object);
        var client = new Mock<BlobServiceClient>();
        client.Setup(value => value.GetBlobContainerClient("bucket")).Returns(container.Object);
        var reader = new AzureBlobRangeReader(client.Object);

        var bytes = await reader.ReadRangeAsync("bucket", "chunk", 0, 64 * 1024 * 1024);

        bytes.Should().Equal(payload);
    }
}
