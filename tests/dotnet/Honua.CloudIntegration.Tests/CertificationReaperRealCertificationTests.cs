// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Amazon;
using Amazon.Batch;
using Amazon.Batch.Model;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Xunit;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-AWS certification lane (#2164) — the run-scoped resource REAPER. A guaranteed
/// <c>finally</c>/teardown protects the happy path, but a crashed runner (OOM, cancelled workflow,
/// power loss between create and delete) can still orphan a resource. This sweeper is the
/// belt-and-braces backstop: it reaps <c>honua-cert-run</c>-tagged resources older than a TTL so
/// cost stays bounded even when a run dies mid-flight.
///
/// It keys STRICTLY off the <see cref="RealAwsCertificationFixture.RunTagKey"/> tag (never a name
/// prefix), so it can NEVER touch the standing certification-stack resources — those share the
/// <c>honua-cert-*</c> NAME prefix but carry no <c>honua-cert-run</c> TAG. Budget is describe/list +
/// get-tagging + delete only.
///
/// The reaper runs LAST: it is filtered into a dedicated <c>always()</c> workflow step by
/// <c>FullyQualifiedName~Reaper</c> AFTER the main certification step (which excludes it with
/// <c>FullyQualifiedName!~Reaper</c>), so it sweeps this run's leftovers plus any prior run's
/// leaked, aged-out resources without racing the cells that create them.
/// </summary>
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

/// <summary>
/// Tag-driven sweeper for leaked real-AWS certification resources (#2164). Reaps only resources
/// carrying <see cref="RealAwsCertificationFixture.RunTagKey"/> that are older than a TTL, so it can
/// never touch the standing certification stack (which shares the name prefix but not the run tag).
/// </summary>
internal static class CertificationResourceReaper
{
    /// <summary>S3 key prefix under which certification artifacts are written and swept.</summary>
    public const string ArtifactPrefix = "cert/";

    /// <summary>
    /// Deregisters every ACTIVE Batch job definition tagged <see cref="RealAwsCertificationFixture.RunTagKey"/>
    /// whose <see cref="RealAwsCertificationFixture.CreatedTagKey"/> stamp is older than <paramref name="ttl"/>.
    /// Job definitions expose no native creation timestamp, so the created-tag is the TTL clock.
    /// </summary>
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

    /// <summary>
    /// Deletes every object under <paramref name="prefix"/> in <paramref name="bucket"/> that is
    /// older than <paramref name="ttl"/> (by S3 <c>LastModified</c>) AND carries the
    /// <see cref="RealAwsCertificationFixture.RunTagKey"/> tag. Object tagging is only fetched for
    /// aged objects to keep the request budget low.
    /// </summary>
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
