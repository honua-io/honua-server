// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public sealed class ProviderEndpointPolicyTests
{
    private static readonly string[] Suffixes = [".batch.azure.com"];

    [UnitTheory]
    [InlineData("https://acct.eastus.batch.azure.com")]
    [InlineData("https://ACCT.EASTUS.BATCH.AZURE.COM/jobs")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://127.0.0.1:8443")]
    [InlineData("http://[::1]:8080")]
    public void Validate_AllowsHttpsSuffixHostsAndLoopback(string url)
    {
        ProviderEndpointPolicy.Validate(url, Suffixes).Should().BeNull();
    }

    [UnitTheory]
    [InlineData("http://acct.eastus.batch.azure.com", "https")]
    [InlineData("https://acct.batch.azure.com.evil.example", "host must end with")]
    [InlineData("https://evilbatch.azure.com", "host must end with")]
    [InlineData("https://batch.azure.com", "host must end with")]
    [InlineData("ftp://localhost/x", "https")]
    [InlineData("https://u:p@acct.eastus.batch.azure.com", "embedded credentials")]
    [InlineData("", "absolute URL")]
    public void Validate_RejectsPlainHttpForeignHostsAndUserInfo(string url, string expectedReason)
    {
        ProviderEndpointPolicy.Validate(url, Suffixes).Should().Contain(expectedReason);
    }
}
