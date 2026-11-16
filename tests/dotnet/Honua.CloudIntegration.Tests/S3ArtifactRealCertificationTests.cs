// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class S3ArtifactRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    private readonly RealAwsCertificationFixture _cert;
    private readonly ITestOutputHelper _output;

    public S3ArtifactRealCertificationTests(RealAwsCertificationFixture cert, ITestOutputHelper output)
    {
        _cert = cert;
        _output = output;
    }

    [SkippableFact]
    public async Task PutThenGet_RoundTripsArtifact_ThroughLiveCertBucket()
    {
        Skip.IfNot(
            _cert.S3ArtifactConfigured,
            "Real-AWS S3 artifact cell not configured (needs HONUA_REALAWS_CERT_ENABLED=true and "
            + "HONUA_REALAWS_CERT_ARTIFACT_BUCKET with credentials present).");

        using var client = CreateClient(_cert.Region);

        var bucket = _cert.ArtifactBucket!;
        var key = $"cert/{_cert.RunId}/artifact-{Guid.NewGuid():N}.txt";
        var payload = $"honua-real-aws-certification-{Guid.NewGuid():N}";

        try
        {
            // 1. Put: write the artifact, tagged with the per-run tag so the reaper can reclaim it.
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = payload,
                TagSet =
                [
                    new Tag { Key = RealAwsCertificationFixture.RunTagKey, Value = _cert.RunId },
                ],
            });

            // 2. Get: read it back and assert the bytes round-trip byte-for-byte.
            using var response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
            });

            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            var roundTripped = await reader.ReadToEndAsync();

            roundTripped.Should().Be(
                payload,
                "the object written to the live certification bucket must be retrievable byte-for-byte");

            // 3. Assert the run tag is present so the reaper contract holds end-to-end.
            var tagging = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = bucket,
                Key = key,
            });
            tagging.Tagging.Should().Contain(
                tag => tag.Key == RealAwsCertificationFixture.RunTagKey && tag.Value == _cert.RunId,
                "every certification-created S3 object must carry the honua-cert-run tag");
        }
        finally
        {
            // 4. Guaranteed teardown: delete the object so the lane leaves zero standing artifacts.
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Best-effort: a leaked object still carries the honua-cert-run tag and is reaped
                // by the TTL sweeper, so a transient delete blip cannot accumulate cost. Log it so a
                // failed teardown is visible in CI rather than silently swallowed.
                _output.WriteLine(
                    $"[cert] best-effort delete of s3://{bucket}/{key} failed: {ex.Message}");
            }
        }
    }

    // Mirrors AwsS3FileStorage.CreateClient for the real-cloud case: a region-scoped client with no
    // ServiceURL override and no static credentials, so the SDK default chain (OIDC in CI) signs.
    private static AmazonS3Client CreateClient(string region)
        => new(new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) });
}
