// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Azure.Storage.Blobs;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

/// <summary>SDK wire regressions against a loopback object server, not cloud qualification.</summary>
public sealed class ConditionalCloudDownloadTests
{
    [Theory]
    [InlineData(CloudStorageProvider.AwsS3, false)]
    [InlineData(CloudStorageProvider.AzureBlob, false)]
    [InlineData(CloudStorageProvider.AwsS3, true)]
    [InlineData(CloudStorageProvider.AzureBlob, true)]
    [Trait("Tier", "Fast")]
    public async Task ConditionalRead_SameSizeReplacement_SendsIfMatchAndRejectsOldGeneration(
        CloudStorageProvider provider, bool range)
    {
        // The AWS SDK validates a full-object ETag as the body's MD5; these are
        // wire fixtures for the two byte strings below, not an integrity policy.
        const string oldTag = "\"27394cb570d875905830131d97c1871c\"";
        const string newTag = "\"25a86c1c7f5ffdfa2cd1f922cdc88668\"";
        var original = "original-generation"u8.ToArray();
        var replacement = "replacement-content"u8.ToArray();
        using var endpoint = AwsS3RangeReaderEndpointTests.FakeObjectEndpoint.Start(original, oldTag);
        var s3Options = new AwsS3Options
        {
            BucketName = "fixtures",
            Region = "us-east-1",
            AccessKeyId = "test",
            SecretAccessKey = "test",
            ForcePathStyle = true,
            ServiceUrl = endpoint.ServiceUrl
        };
        var connectionString = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={Convert.ToBase64String(new byte[32])};BlobEndpoint={endpoint.ServiceUrl}/devstoreaccount1;";
        var options = Options.Create(new CloudStorageOptions
        {
            Provider = provider,
            AwsS3 = s3Options,
            AzureBlob = new AzureBlobOptions { ConnectionString = connectionString, ContainerName = "fixtures" }
        });
        ICloudFileStorage storage = provider == CloudStorageProvider.AwsS3
            ? new AwsS3FileStorage(options, NullLogger<AwsS3FileStorage>.Instance, new InMemoryUploadProgressStore())
            : new AzureBlobFileStorage(options, NullLogger<AzureBlobFileStorage>.Instance, new InMemoryUploadProgressStore());
        using var s3Client = AwsS3FileStorage.CreateClient(s3Options);
        ICloudRangeReader rangeReader = provider == CloudStorageProvider.AwsS3
            ? new AwsS3RangeReader(s3Client)
            : new AzureBlobRangeReader(new BlobServiceClient(connectionString));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        async Task<byte[]> Read(string etag)
        {
            if (range)
            {
                return await rangeReader.ReadRangeAsync("fixtures", "artifact.bin", 0, 7, etag, cancellation.Token);
            }
            await using var stream = await storage.DownloadIfMatchAsync("artifact.bin", etag, cancellation.Token);
            stream.Should().NotBeNull();
            using var bytes = new MemoryStream();
            await stream!.CopyToAsync(bytes, cancellation.Token);
            return bytes.ToArray();
        }

        (await Read(oldTag)).Should().Equal(range ? original[..7] : original);
        endpoint.Replace(replacement, newTag);
        var staleRead = async () => await Read(oldTag);
        await staleRead.Should().ThrowAsync<CloudFileRevisionChangedException>();
        (await Read(newTag)).Should().Equal(range ? replacement[..7] : replacement);
        endpoint.Requests.Select(request => request.Headers["If-Match"]).Should().Equal(oldTag, oldTag, newTag);
    }
}
