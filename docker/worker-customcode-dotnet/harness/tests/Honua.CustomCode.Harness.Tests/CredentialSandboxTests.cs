// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.CustomCode.Harness;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class CredentialSandboxTests
{
    [Fact]
    public void StripCredentialEnv_RemovesTokenAndImdsVars()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HONUA_JOB_TOKEN"] = "scoped",
            ["AWS_CONTAINER_CREDENTIALS_RELATIVE_URI"] = "/v2/credentials/abc",
            ["ECS_CONTAINER_METADATA_URI_V4"] = "http://169.254.170.2/v4",
            ["AWS_ACCESS_KEY_ID"] = "AKIA...",
            ["HONUA_BASE_URL"] = "https://api.honua.test", // must be kept
            ["PATH"] = "/usr/bin",                          // must be kept
        };

        var removed = CredentialSandbox.StripCredentialEnv(env);

        removed.Should().Contain(new[]
        {
            "HONUA_JOB_TOKEN",
            "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
            "ECS_CONTAINER_METADATA_URI_V4",
            "AWS_ACCESS_KEY_ID",
        });
        env.Should().NotContainKeys("HONUA_JOB_TOKEN", "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "ECS_CONTAINER_METADATA_URI_V4", "AWS_ACCESS_KEY_ID");
        env.Should().ContainKey("HONUA_BASE_URL");
        env.Should().ContainKey("PATH");
    }

    [Fact]
    public void AssertCredentialsStripped_Throws_WhenTokenRemains()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HONUA_JOB_TOKEN"] = "still-here",
        };

        var act = () => CredentialSandbox.AssertCredentialsStripped(env);

        act.Should().Throw<InvalidOperationException>().WithMessage("*credential env not fully stripped*");
    }

    [Fact]
    public void AssertCredentialsStripped_Passes_WhenClean()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HONUA_BASE_URL"] = "https://api.honua.test",
        };

        var act = () => CredentialSandbox.AssertCredentialsStripped(env);

        act.Should().NotThrow();
    }

    [Fact]
    public void BuildScopedClient_UsesInjectedFactory_AndRequiresBaseUrlAndToken()
    {
        var built = false;
        var client = CredentialSandbox.BuildScopedClient("https://api.honua.test", "scoped", (url, token) =>
        {
            built = true;
            url.Should().Be("https://api.honua.test");
            token.Should().Be("scoped");
            return new object();
        });

        built.Should().BeTrue();
        client.Should().NotBeNull();

        var noToken = () => CredentialSandbox.BuildScopedClient("https://api.honua.test", "", (_, _) => new object());
        noToken.Should().Throw<ArgumentException>();
    }
}
