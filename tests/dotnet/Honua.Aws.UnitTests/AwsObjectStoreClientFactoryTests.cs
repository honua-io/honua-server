// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Honua.Aws.UnitTests;

public sealed class AwsObjectStoreClientFactoryTests
{
    internal const string TrustedExternalId = "3f9c0d2e7b1a4c5d8e6f0a1b2c3d4e5f3f9c0d2e7b1a4c5d8e6f0a1b2c3d4e5f";

    internal static readonly ObjectStorageConnection Customer = new()
    {
        Id = Guid.Parse("a3c5e7f9-1b2d-4e6f-8a0c-2e4f6a8c0e2a"),
        Name = "Customer",
        Bucket = "customer-bucket",
        Prefix = "layers/",
        Region = "eu-west-1",
        RoleArn = "arn:aws:iam::123456789012:role/honua-connection-customer",
        ExternalId = TrustedExternalId,
    };

    [UnitTest]
    public void AssumeRoleRequest_BindsTheExternalIdAReadOnlySessionPolicyAndFifteenMinutes()
    {
        var request = AwsObjectStoreClientFactory.BuildAssumeRoleRequest(Customer);

        Assert.Equal(Customer.RoleArn, request.RoleArn);
        Assert.Equal(TrustedExternalId, request.ExternalId);
        Assert.Equal(900, request.DurationSeconds);
        Assert.Equal(AwsObjectStoreClientFactory.RoleSessionName, request.RoleSessionName);
        using var policy = JsonDocument.Parse(request.Policy);
        var actions = policy.RootElement.GetProperty("Statement").EnumerateArray()
            .Select(statement => statement.GetProperty("Action").GetString()!)
            .ToArray();
        Assert.Equal(["s3:GetObject", "s3:ListBucket", "s3:GetBucketLocation", "s3:GetBucketRequestPayment"], actions);
    }

    [UnitTest]
    public void AssumeRoleRequest_RefusesAConnectionWithoutABinding()
    {
        var unbound = Customer with { ExternalId = null };

        var refused = Assert.Throws<ImportObjectStoreException>(() => AwsObjectStoreClientFactory.BuildAssumeRoleRequest(unbound));

        Assert.Equal(ImportObjectStoreProblems.Forbidden, refused.Code);
    }

    // LocalStack Community grants every AssumeRole, so the trust-policy denial runs against a double.
    [UnitTest]
    public async Task RoleThatTrustsAnotherExternalId_IsForbiddenWithoutEchoingIt()
    {
        using var sts = new TrustPolicyStsDouble("a-different-external-id");
        using var host = new FakeS3();
        using var factory = new AwsObjectStoreClientFactory(new AwsObjectStoreClientSeams { HostClient = host, Sts = sts });

        var refused = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => factory.LeaseAsync(Customer, CancellationToken.None));

        Assert.Equal(ImportObjectStoreProblems.Forbidden, refused.Code);
        Assert.DoesNotContain(TrustedExternalId, refused.Message, StringComparison.Ordinal);
        Assert.Equal(TrustedExternalId, Assert.Single(sts.Requests).ExternalId);
    }

    [UnitTest]
    public async Task CustomerConnection_ReadsThroughASessionClientNotTheHostIdentity()
    {
        using var sts = new TrustPolicyStsDouble(TrustedExternalId);
        using var host = new FakeS3();
        using var session = new FakeS3();
        AWSCredentials? issued = null;
        using var factory = new AwsObjectStoreClientFactory(new AwsObjectStoreClientSeams
        {
            HostClient = host,
            Sts = sts,
            SessionClient = credentials =>
            {
                issued = credentials;
                return session;
            },
        });

        using var lease = await factory.LeaseAsync(Customer, CancellationToken.None);

        Assert.Same(session, lease.Client);
        Assert.IsType<SessionAWSCredentials>(issued);
    }

    [UnitTest]
    public async Task BuiltInConnection_UsesTheHostIdentityWithoutAssumingARole()
    {
        using var sts = new TrustPolicyStsDouble(TrustedExternalId);
        using var host = new FakeS3();
        using var factory = new AwsObjectStoreClientFactory(new AwsObjectStoreClientSeams { HostClient = host, Sts = sts });

        using var lease = await factory.LeaseAsync(Customer with { BuiltIn = true, RoleArn = null }, CancellationToken.None);

        Assert.Same(host, lease.Client);
        Assert.Empty(sts.Requests);
    }

    [UnitTest]
    public async Task EndpointOverride_IsRefusedInProduction()
    {
        using var host = new FakeS3();
        using var factory = new AwsObjectStoreClientFactory(new AwsObjectStoreClientSeams
        {
            HostClient = host,
            Options = new ImportObjectStoreOptions { ServiceUrl = "http://localhost:4566" },
            Environment = new StaticEnvironment(Environments.Production),
        });

        var refused = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => factory.LeaseAsync(Customer with { BuiltIn = true }, CancellationToken.None));

        Assert.Contains("Production", refused.Message, StringComparison.Ordinal);
    }

    internal sealed class TrustPolicyStsDouble(string trustedExternalId) : AmazonSecurityTokenServiceClient(
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

    private sealed class StaticEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Honua.Aws.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
