// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;

namespace Honua.Postgres.Tests.Features.Security;

[Collection("Security")]
public class AwsSecretsManagerResolverEndpointValidationTests
{
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
