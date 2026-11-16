// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Amazon;
using Amazon.Batch;
using Amazon.Batch.Model;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Xunit;

namespace Honua.CloudIntegration.Tests;

[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class CertificationReaperRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    // Only reap resources older than this so a concurrent run's in-flight resources are never
    // reaped out from under it. Comfortably longer than any single cell's budget.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly RealAwsCertificationFixture _cert;

    public CertificationReaperRealCertificationTests(RealAwsCertificationFixture cert)
    {
        _cert = cert;
    }

    [SkippableFact]
    public async Task Reaper_SweepsAgedTaggedResources_WithoutTouchingTheStandingStack()
    {
        Skip.IfNot(
            _cert.Enabled,
            "Real-AWS certification lane disabled (set HONUA_REALAWS_CERT_ENABLED=true with credentials present).");

        var now = DateTimeOffset.UtcNow;
        var region = RegionEndpoint.GetBySystemName(_cert.Region);

        using var batch = new AmazonBatchClient(region);
        var reapedJobDefinitions = await CertificationResourceReaper
            .ReapJobDefinitionsAsync(batch, Ttl, now);
        reapedJobDefinitions.Should().BeGreaterThanOrEqualTo(
            0, "reaping job definitions must complete without error");

        if (!string.IsNullOrWhiteSpace(_cert.ArtifactBucket))
        {
            using var s3 = new AmazonS3Client(new AmazonS3Config { RegionEndpoint = region });
            var reapedObjects = await CertificationResourceReaper
                .ReapArtifactsAsync(s3, _cert.ArtifactBucket!, CertificationResourceReaper.ArtifactPrefix, Ttl, now);
            reapedObjects.Should().BeGreaterThanOrEqualTo(
                0, "reaping S3 artifacts must complete without error");
        }
    }
}

internal static class CertificationResourceReaper
{
    public const string ArtifactPrefix = "cert/";

    public static async Task<int> ReapJobDefinitionsAsync(
        IAmazonBatch batch,
        TimeSpan ttl,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var reaped = 0;
        string? nextToken = null;
        do
        {
            var response = await batch.DescribeJobDefinitionsAsync(
                new DescribeJobDefinitionsRequest { Status = "ACTIVE", NextToken = nextToken },
                cancellationToken).ConfigureAwait(false);

            foreach (var definition in response.JobDefinitions ?? [])
            {
                if (!IsReapable(definition.Tags, now, ttl))
                {
                    continue;
                }

                await batch.DeregisterJobDefinitionAsync(
                    new DeregisterJobDefinitionRequest { JobDefinition = definition.JobDefinitionArn },
                    cancellationToken).ConfigureAwait(false);
                reaped++;
            }

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return reaped;
    }

    public static async Task<int> ReapArtifactsAsync(
        IAmazonS3 s3,
        string bucket,
        string prefix,
        TimeSpan ttl,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        var reaped = 0;
        string? continuationToken = null;
        do
        {
            var response = await s3.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken,
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var entry in response.S3Objects ?? [])
            {
                var lastModified = entry.LastModified;
                if (!lastModified.HasValue || now - new DateTimeOffset(lastModified.Value, TimeSpan.Zero) < ttl)
                {
                    continue;
                }

                var tagging = await s3.GetObjectTaggingAsync(
                    new GetObjectTaggingRequest { BucketName = bucket, Key = entry.Key },
                    cancellationToken).ConfigureAwait(false);

                if (tagging.Tagging is null
                    || !tagging.Tagging.Exists(tag => tag.Key == RealAwsCertificationFixture.RunTagKey))
                {
                    continue;
                }

                await s3.DeleteObjectAsync(
                    new DeleteObjectRequest { BucketName = bucket, Key = entry.Key },
                    cancellationToken).ConfigureAwait(false);
                reaped++;
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));

        return reaped;
    }

    private static bool IsReapable(Dictionary<string, string>? tags, DateTimeOffset now, TimeSpan ttl)
    {
        if (tags is null
            || !tags.ContainsKey(RealAwsCertificationFixture.RunTagKey)
            || !tags.TryGetValue(RealAwsCertificationFixture.CreatedTagKey, out var createdRaw)
            || !long.TryParse(createdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var createdEpoch))
        {
            // Missing run tag → not ours. Missing/unparseable created-tag → cannot age it safely; the
            // guaranteed finally already deregisters happy-path resources, so leave it rather than
            // risk deleting something whose age we cannot prove.
            return false;
        }

        var created = DateTimeOffset.FromUnixTimeSeconds(createdEpoch);
        return now - created >= ttl;
    }
}
