// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

[Collection("Security")]
public class AwsSecretsManagerResolverEndpointValidationTests
{
    // Bootstrap (HttpClient-based) constructor added for honua-server#3011: Program.cs's Redis
    // ConnectionMultiplexer wiring must resolve aws:secretsmanager: connection-string references
    // before WebApplicationBuilder.Build() runs, so it cannot pull the named resilient clients from
    // an IHttpClientFactory the way the DI-registered instance (used for the Postgres
    // DefaultConnection string) does. These tests only prove the bootstrap constructor produces a
    // working resolver for reference-recognition purposes — no network call is made.
    [Fact]
    public void BootstrapConstructor_ArnStyleSecretReference_CanResolveReturnsTrue()
    {
        using var secretsClient = new HttpClient();
        using var metadataClient = new HttpClient();
        var resolver = new AwsSecretsManagerResolver(
            secretsClient,
            metadataClient,
            NullLogger<AwsSecretsManagerResolver>.Instance);

        var canResolve = resolver.CanResolve("aws:secretsmanager:arn:aws:secretsmanager:us-east-1:123456789012:secret:demo-redis-abc123");

        Assert.True(canResolve);
        Assert.Equal("aws", resolver.ProviderName);
    }

    [Fact]
    public void BootstrapConstructor_NonAwsSecretReference_CanResolveReturnsFalse()
    {
        using var secretsClient = new HttpClient();
        using var metadataClient = new HttpClient();
        var resolver = new AwsSecretsManagerResolver(
            secretsClient,
            metadataClient,
            NullLogger<AwsSecretsManagerResolver>.Instance);

        var canResolve = resolver.CanResolve("localhost:6379");

        Assert.False(canResolve);
    }

    [SecurityTest]
    [Theory]
    [InlineData("http://169.254.170.2/v2/credentials")]
    [InlineData("http://169.254.170.23/v1/credentials")]
    [InlineData("http://127.0.0.1:8080/credentials")]
    [InlineData("https://localhost/credentials")]
    [InlineData("http://[::1]/credentials")]
    [InlineData("http://[fd00:ec2::23]/credentials")]
    public void IsAllowedEcsCredentialsEndpoint_TrustedLocalEndpoints_ReturnsTrue(string uri)
    {
        var endpoint = new Uri(uri);

        var isAllowed = AwsSecretsManagerResolver.IsAllowedEcsCredentialsEndpoint(endpoint);

        Assert.True(isAllowed);
    }

    [SecurityTest]
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://203.0.113.10/credentials")]
    [InlineData("https://example.com/credentials")]
    [InlineData("ftp://127.0.0.1/credentials")]
    [InlineData("http://user:pass@127.0.0.1/credentials")]
    public void IsAllowedEcsCredentialsEndpoint_UntrustedEndpoints_ReturnsFalse(string uri)
    {
        var endpoint = new Uri(uri);

        var isAllowed = AwsSecretsManagerResolver.IsAllowedEcsCredentialsEndpoint(endpoint);

        Assert.False(isAllowed);
    }
}
