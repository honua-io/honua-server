// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

/// <summary>
/// Integration tests for AwsS3FileStorage using a local emulator.
/// </summary>
[Collection("Emulators")]
public sealed class AwsS3FileStorageTests
{
    private const string BucketEnv = "HONUA_TEST_S3_BUCKET";
    private const string RegionEnv = "HONUA_TEST_S3_REGION";
    private const string AccessKeyEnv = "HONUA_TEST_S3_ACCESS_KEY";
    private const string SecretKeyEnv = "HONUA_TEST_S3_SECRET_KEY";
    private const string ServiceUrlEnv = "HONUA_TEST_S3_SERVICE_URL";
    private const string ForcePathStyleEnv = "HONUA_TEST_S3_FORCE_PATH_STYLE";

    [EmulatorTest(BucketEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv, ServiceUrlEnv)]
    public async Task UploadDownloadDelete_RoundTripsContent()
    {
        var storage = await CreateStorageAsync();
        var content = "S3 emulator content"u8.ToArray();
        var request = new ByteArrayUploadRequest
        {
            Content = content,
            FileName = "s3-test.txt",
            ContentType = "text/plain"
        };

        string? fileId = null;
        try
        {
            var result = await storage.UploadAsync(request);
            result.Success.Should().BeTrue();
            result.File.Should().NotBeNull();
            fileId = result.File!.FileId;

            var downloaded = await storage.DownloadBytesAsync(fileId);
            downloaded.Should().NotBeNull();
            downloaded.Should().Equal(content);

            var metadata = await storage.GetMetadataAsync(fileId);
            metadata.Should().NotBeNull();
            metadata!.FileName.Should().Be("s3-test.txt");
        }
        finally
        {
            if (!string.IsNullOrEmpty(fileId))
            {
                await storage.DeleteAsync(fileId);
            }
        }
    }

    private static async Task<AwsS3FileStorage> CreateStorageAsync()
    {
        var options = GetOptionsOrSkip();
        await EnsureBucketExistsAsync(options.AwsS3!);
        return new AwsS3FileStorage(
            Options.Create(options),
            NullLogger<AwsS3FileStorage>.Instance,
            new InMemoryUploadProgressStore());
    }

    private static CloudStorageOptions GetOptionsOrSkip()
    {
        var bucket = GetRequiredEnv(BucketEnv);
        var region = GetRequiredEnv(RegionEnv);
        var accessKey = GetRequiredEnv(AccessKeyEnv);
        var secretKey = GetRequiredEnv(SecretKeyEnv);
        var serviceUrl = Environment.GetEnvironmentVariable(ServiceUrlEnv);
        var forcePathStyle = bool.TryParse(Environment.GetEnvironmentVariable(ForcePathStyleEnv), out var parsed)
            && parsed;

        return new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = bucket,
                Region = region,
                AccessKeyId = accessKey,
                SecretAccessKey = secretKey,
                ServiceUrl = serviceUrl,
                ForcePathStyle = forcePathStyle
            }
        };
    }

    private static string GetRequiredEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required for S3 emulator tests.");
        }

        return value;
    }

    private static async Task EnsureBucketExistsAsync(AwsS3Options options)
    {
        using var client = CreateClient(options);
        var exists = await AmazonS3Util.DoesS3BucketExistV2Async(client, options.BucketName);
        if (!exists)
        {
            await client.PutBucketAsync(new PutBucketRequest
            {
                BucketName = options.BucketName
            });
        }
    }

    private static AmazonS3Client CreateClient(AwsS3Options options)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region),
            ForcePathStyle = options.ForcePathStyle
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        return new AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config);
    }
}
