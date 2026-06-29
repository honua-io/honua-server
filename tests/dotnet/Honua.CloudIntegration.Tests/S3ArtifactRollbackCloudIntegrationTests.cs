// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Free-tier (LocalStack Community) emulated scenario (#2166) that broadens the matrix beyond the
/// single-object round-trip in <see cref="S3ArtifactRoundTripCloudIntegrationTests"/> into a
/// deploy-artifact ROLLBACK path: a versioned bucket publishes artifact v1, then v2 (so v2 is the
/// live version), and a rollback then promotes v1 back to live by deleting the bad v2 version. This
/// exercises the S3 versioning surface (enable versioning, list object versions, delete by
/// version id) that an artifact-store rollback relies on, against a REAL emulated S3 endpoint via
/// the <see cref="LocalStackFixture"/>'s edge ServiceURL.
///
/// Gated like the other emulated scenarios: when Docker is unavailable the fixture reports
/// <see cref="LocalStackFixture.Available"/> = false and this test skips (never fails).
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CloudIntegration)]
public sealed class S3ArtifactRollbackCloudIntegrationTests : IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _localStack;

    public S3ArtifactRollbackCloudIntegrationTests(LocalStackFixture localStack)
    {
        _localStack = localStack;
    }

    [SkippableFact]
    public async Task RollbackToPreviousVersion_RestoresPriorArtifact_ThroughCommunityS3()
    {
        Skip.IfNot(_localStack.Available, "LocalStack Community not available (Docker daemon absent).");

        using var client = CreateClient(_localStack.ServiceUrl);

        var bucket = $"honua-ci-rollback-{Guid.NewGuid():N}";
        const string key = "deploy/manifest.json";
        var v1Payload = $"artifact-v1-{Guid.NewGuid():N}";
        var v2Payload = $"artifact-v2-{Guid.NewGuid():N}";

        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        try
        {
            // Enable versioning so each publish retains a recoverable prior version — the
            // precondition every immutable-artifact rollback strategy depends on.
            await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled }
            });

            // 1. Publish v1, then v2 (the bad release) over the same key.
            await PutAsync(client, bucket, key, v1Payload);
            await PutAsync(client, bucket, key, v2Payload);

            // The live (latest) version is now v2.
            (await GetAsync(client, bucket, key)).Should().Be(
                v2Payload, "the most recent publish is the live version before rollback");

            // 2. Enumerate object versions; both publishes must be retained for rollback.
            var versions = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket,
                Prefix = key
            });
            var keyVersions = versions.Versions.Where(v => v.Key == key).ToList();
            keyVersions.Should().HaveCount(2, "both the prior and current artifact versions must be retained");

            var liveVersionId = keyVersions.Single(v => v.IsLatest == true).VersionId;

            // 3. Roll back by deleting the live (bad) version; S3 promotes the prior version to live.
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = liveVersionId
            });

            // 4. The live artifact is once again v1 — rollback succeeded.
            (await GetAsync(client, bucket, key)).Should().Be(
                v1Payload, "deleting the bad version must promote the prior artifact back to live");
        }
        finally
        {
            await PurgeBucketAsync(client, bucket);
        }
    }

    private static async Task PutAsync(AmazonS3Client client, string bucket, string key, string payload)
        => await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = payload
        });

    private static async Task<string> GetAsync(AmazonS3Client client, string bucket, string key)
    {
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key
        });
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task PurgeBucketAsync(AmazonS3Client client, string bucket)
    {
        // A versioned bucket can only be deleted once every version (and delete marker) is removed.
        // Best-effort cleanup so the ephemeral emulator does not accumulate state.
        try
        {
            var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
            foreach (var version in versions.Versions)
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = version.Key,
                    VersionId = version.VersionId
                });
            }

            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        }
        catch
        {
            // The LocalStack container is ephemeral CI infrastructure; cleanup is best-effort.
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
