// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Capabilities;

/// <summary>
/// Unit coverage for the immutable deployment-revision grammar and configuration
/// resolution that back the manifest and streaming capability projections (#3038, REQ-004).
/// </summary>
public sealed class DeploymentIdentityTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef01234567";
    private const string ValidDigest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(ValidSha, true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567", false)] // uppercase is not the canonical form
    [InlineData("0123456789abcdef0123456789abcdef0123456", false)] // 39 chars
    [InlineData("0123456789abcdef0123456789abcdef012345678", false)] // 41 chars
    [InlineData("v1.2.3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCommitSha_AcceptsOnlyFullLowercaseHex(string? value, bool expected)
        => HonuaDeploymentIdentity.IsCommitSha(value).Should().Be(expected);

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(ValidDigest, true)]
    [InlineData("sha256:short", false)]
    [InlineData("sha512:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData(ValidSha, false)]
    [InlineData(null, false)]
    public void IsImageDigest_AcceptsOnlySha256Digests(string? value, bool expected)
        => HonuaDeploymentIdentity.IsImageDigest(value).Should().Be(expected);

    [UnitTest]
    public void GetReleaseVersion_StripsSemVerBuildMetadata()
    {
        // The assembly under test is stamped by SourceLink in a git-visible build
        // (1.0.0+<sha>) and left bare otherwise; either way the release version must
        // never carry the build metadata that identifies the commit.
        var version = HonuaDeploymentIdentity.GetReleaseVersion(typeof(DeploymentIdentity).Assembly);

        version.Should().NotBeNullOrWhiteSpace();
        version.Should().NotContain("+");
    }

    [UnitTest]
    public void Resolve_PrefersImageDigestOverCommitSha()
    {
        var identity = Build(new Dictionary<string, string?>
        {
            ["Deployment:ImageDigest"] = ValidDigest,
            ["Deployment:Revision"] = ValidSha
        });

        identity.Revision.Should().Be(ValidDigest);
        identity.RevisionSource.Should().Be(HonuaDeploymentIdentity.ImageDigestSource);
    }

    [UnitTest]
    public void Resolve_UsesCommitShaWhenNoDigestIsConfigured()
    {
        var identity = Build(new Dictionary<string, string?>
        {
            ["Deployment:Revision"] = ValidSha
        });

        identity.Revision.Should().Be(ValidSha);
        identity.RevisionSource.Should().Be(HonuaDeploymentIdentity.CommitShaSource);
    }

    [UnitTest]
    public void Resolve_NormalizesCasingAndSurroundingWhitespace()
    {
        var identity = Build(new Dictionary<string, string?>
        {
            ["Deployment:Revision"] = "  0123456789ABCDEF0123456789ABCDEF01234567  "
        });

        identity.Revision.Should().Be(ValidSha);
    }

    [UnitTest]
    public void Resolve_RejectsMalformedValuesRatherThanEchoingThem()
    {
        var identity = Build(new Dictionary<string, string?>
        {
            ["Deployment:ImageDigest"] = "sha256:short",
            ["Deployment:Revision"] = "release-2026-07"
        });

        // Falls through to the process-level resolution, which may legitimately produce a
        // SourceLink-stamped SHA. What must never happen is the free-text value surfacing.
        identity.Revision.Should().NotBe("sha256:short");
        identity.Revision.Should().NotBe("release-2026-07");
        if (identity.Revision is not null)
        {
            (HonuaDeploymentIdentity.IsCommitSha(identity.Revision)
                || HonuaDeploymentIdentity.IsImageDigest(identity.Revision)).Should().BeTrue();
        }
    }

    [UnitTest]
    public void Resolve_KeepsReleaseVersionSeparateFromRevision()
    {
        var identity = Build(new Dictionary<string, string?>
        {
            ["Deployment:Revision"] = ValidSha
        });

        identity.ReleaseVersion.Should().NotBeNullOrWhiteSpace();
        identity.ReleaseVersion.Should().NotBe(identity.Revision);
    }

    private static DeploymentIdentity Build(Dictionary<string, string?> settings)
        => new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
}
