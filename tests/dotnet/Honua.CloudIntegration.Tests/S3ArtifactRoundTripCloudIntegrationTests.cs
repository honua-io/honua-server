// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Free-tier (LocalStack Community) scenario (#2163): proves an S3 artifact round-trip — create
/// bucket, put object, get object, delete — against a REAL emulated S3 endpoint via the
/// <see cref="LocalStackFixture"/>'s edge ServiceURL. This exercises the same SDK
/// ServiceURL-override + path-style seam Honua's <c>AwsS3FileStorage</c> uses to target a custom
/// endpoint, so the LocalStack Community fixture backs a runnable test rather than sitting unused.
///
/// Gated like the kind lifecycle test: when Docker is unavailable the fixture reports
/// <see cref="LocalStackFixture.Available"/> = false and this test skips (never fails).
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CloudIntegration)]
public sealed class S3ArtifactRoundTripCloudIntegrationTests : IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _localStack;

    public S3ArtifactRoundTripCloudIntegrationTests(LocalStackFixture localStack)
    {
        _localStack = localStack;
    }

    [SkippableFact]
    public async Task PutThenGet_RoundTripsArtifact_ThroughCommunityS3()
    {
        Skip.IfNot(_localStack.Available, "LocalStack Community not available (Docker daemon absent).");

        using var client = CreateClient(_localStack.ServiceUrl);

        var bucket = $"honua-ci-{Guid.NewGuid():N}";
        var key = "deploy/artifact.txt";
        var payload = $"honua-cloud-integration-{Guid.NewGuid():N}";

        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        try
        {
            // 1. Put: write the artifact through the emulated S3 endpoint.
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = payload
            });

            // 2. Get: read it back and assert the bytes round-trip unchanged.
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            });

            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            var roundTripped = await reader.ReadToEndAsync();

            roundTripped.Should().Be(
                payload,
                "the object written to the emulated S3 endpoint must be retrievable byte-for-byte");
        }
        finally
        {
            // 3. Clean up: best-effort delete so the ephemeral emulator does not accumulate state.
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
                await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
            }
            catch
            {
                // The LocalStack container is ephemeral CI infrastructure; cleanup is best-effort.
            }
        }
    }

    private static AmazonS3Client CreateClient(string serviceUrl)
    {
        // Mirror the AwsS3FileStorage seam: explicit ServiceURL targets the emulator edge endpoint
        // while ForcePathStyle keeps virtual-host bucket addressing off (LocalStack serves
        // path-style by default). Static "test"/"test" credentials satisfy SigV4 signing.
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = LocalStackFixture.Region,
        };

        return new AmazonS3Client(
            LocalStackFixture.AccessKeyId,
            LocalStackFixture.SecretAccessKey,
            config);
    }
}
