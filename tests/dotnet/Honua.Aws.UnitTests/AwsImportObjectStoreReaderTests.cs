// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Honua.Core.Features.Import.ObjectStore;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Aws.UnitTests;

public sealed class AwsImportObjectStoreReaderTests
{
    private const string RoleArn = "arn:aws:iam::123456789012:role/honua-import";
    private const string TrustedExternalId = "ext-from-config";

    [UnitTest]
    public void BuildAssumeRoleRequest_SetsExternalIdOnlyWhenConfigured()
    {
        var withId = AwsImportObjectStoreReader.BuildAssumeRoleRequest(
            "arn:aws:iam::123456789012:role/honua-import",
            "external-from-config");
        Assert.Equal("external-from-config", withId.ExternalId);
        Assert.Equal(AwsImportObjectStoreReader.RoleSessionName, withId.RoleSessionName);
        Assert.Equal(900, withId.DurationSeconds);

        var withoutId = AwsImportObjectStoreReader.BuildAssumeRoleRequest(
            "arn:aws:iam::123456789012:role/honua-import",
            "  ");
        Assert.True(string.IsNullOrEmpty(withoutId.ExternalId));
    }

    // LocalStack Community grants every AssumeRole, so this denial cannot execute in the free-tier
    // emulator lane. The double applies the role's sts:ExternalId trust condition the way AWS does.
    [UnitTest]
    public async Task RoleThatRequiresAnExternalId_IsForbiddenWithoutTheConfiguredId()
    {
        using var sts = new TrustPolicyStsDouble(TrustedExternalId);
        using var s3 = new CountingS3();
        using var reader = new AwsImportObjectStoreReader(s3, sts, Options(externalId: null));

        var forbidden = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => reader.HeadAsync(Location(), CancellationToken.None));

        Assert.Equal(ImportObjectStoreProblems.Forbidden, forbidden.Code);
        Assert.DoesNotContain(TrustedExternalId, forbidden.Message, StringComparison.Ordinal);
        var sent = Assert.Single(sts.Requests);
        Assert.Equal(RoleArn, sent.RoleArn);
        Assert.True(string.IsNullOrEmpty(sent.ExternalId));
        Assert.Equal(0, s3.Heads);
    }

    [UnitTest]
    public async Task RoleThatRequiresAnExternalId_IsForbiddenWithAWrongConfiguredId()
    {
        using var sts = new TrustPolicyStsDouble(TrustedExternalId);
        using var s3 = new CountingS3();
        using var reader = new AwsImportObjectStoreReader(s3, sts, Options(externalId: "not-the-trusted-id"));

        var forbidden = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => reader.HeadAsync(Location(), CancellationToken.None));

        Assert.Equal(ImportObjectStoreProblems.Forbidden, forbidden.Code);
        Assert.DoesNotContain("not-the-trusted-id", forbidden.Message, StringComparison.Ordinal);
        Assert.Equal("not-the-trusted-id", Assert.Single(sts.Requests).ExternalId);
        Assert.Equal(0, s3.Heads);
    }

    private static StaticOptions Options(string? externalId)
        => new(new ImportObjectStoreOptions
        {
            AllowedBuckets = ["customer-bucket"],
            AssumeRoles = [new ImportObjectStoreRoleOptions { RoleArn = RoleArn, ExternalId = externalId }],
        });

    private static ImportObjectLocation Location()
        => new()
        {
            Bucket = "customer-bucket",
            Key = "layers/assumed.geojson",
            AssumeRoleArn = RoleArn,
            SourceUri = "s3://customer-bucket/layers/assumed.geojson",
        };

    /// <summary>
    /// STS that evaluates a <c>StringEquals sts:ExternalId</c> trust condition: AssumeRole without
    /// the trusted id is refused with AccessDenied, as AWS does. It never reaches the network.
    /// </summary>
    private sealed class TrustPolicyStsDouble(string trustedExternalId) : AmazonSecurityTokenServiceClient(
        new BasicAWSCredentials("unit", "unit"),
        new AmazonSecurityTokenServiceConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public List<AssumeRoleRequest> Requests { get; } = [];

        public override Task<AssumeRoleResponse> AssumeRoleAsync(
            AssumeRoleRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (!string.Equals(request.ExternalId, trustedExternalId, StringComparison.Ordinal))
            {
                throw new AmazonSecurityTokenServiceException(
                    $"User is not authorized to perform: sts:AssumeRole on resource: {request.RoleArn}")
                {
                    ErrorCode = "AccessDenied",
                    StatusCode = HttpStatusCode.Forbidden,
                };
            }

            return Task.FromResult(new AssumeRoleResponse
            {
                Credentials = new Credentials
                {
                    AccessKeyId = "ASIAUNITTESTDOUBLE",
                    SecretAccessKey = "unit",
                    SessionToken = "unit",
                    Expiration = DateTime.UtcNow.AddMinutes(15),
                },
            });
        }
    }

    /// <summary>Counts object HEADs so a refused role is shown never to reach S3.</summary>
    private sealed class CountingS3() : AmazonS3Client(
        new BasicAWSCredentials("unit", "unit"),
        new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public int Heads { get; private set; }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken = default)
        {
            Heads++;
            throw new InvalidOperationException("A refused role must not reach S3.");
        }
    }

    private sealed class StaticOptions(ImportObjectStoreOptions value) : IOptionsMonitor<ImportObjectStoreOptions>
    {
        public ImportObjectStoreOptions CurrentValue => value;

        public ImportObjectStoreOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ImportObjectStoreOptions, string?> listener) => null;
    }
}
