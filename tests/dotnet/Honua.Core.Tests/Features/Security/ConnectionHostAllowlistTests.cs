// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Security;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Security;

/// <summary>
/// Unit tests for the outbound data-source connection host allowlist (#354).
/// </summary>
public sealed class ConnectionHostAllowlistTests
{
    private static ConnectionHostAllowlist Create(
        ConnectionHostAllowlistOptions options,
        Func<string, IPAddress[]>? resolver = null)
    {
        return new ConnectionHostAllowlist(
            options,
            (host, _) => Task.FromResult(resolver?.Invoke(host) ?? Array.Empty<IPAddress>()));
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenPolicyDisabled_AllowsAnyHost()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions());

        var decision = await allowlist.EvaluateAsync("anything.example.com");

        allowlist.IsEnforced.Should().BeFalse();
        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task EvaluateAsync_WithAllowlist_AllowsExactMatch_CaseInsensitive()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            AllowedHosts = ["db.internal.example.com"]
        });

        var decision = await allowlist.EvaluateAsync("DB.Internal.Example.com");

        allowlist.IsEnforced.Should().BeTrue();
        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task EvaluateAsync_WithAllowlist_BlocksHostNotInList()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            AllowedHosts = ["db.internal.example.com"]
        });

        var decision = await allowlist.EvaluateAsync("evil.attacker.example");

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("allowlist");
    }

    [UnitTest]
    public async Task EvaluateAsync_WithWildcardSuffix_AllowsSubdomain()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            AllowedHosts = ["*.rds.amazonaws.com"]
        });

        var decision = await allowlist.EvaluateAsync("prod-db.abc123.us-east-1.rds.amazonaws.com");

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task EvaluateAsync_WithWildcardSuffix_BlocksBareSuffix()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            AllowedHosts = ["*.rds.amazonaws.com"]
        });

        var decision = await allowlist.EvaluateAsync("rds.amazonaws.com");

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_WithEmptyHost_WhenEnforced_IsDenied()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            AllowedHosts = ["db.example.com"]
        });

        var decision = await allowlist.EvaluateAsync("   ");

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("required");
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_RejectsLoopbackLiteral()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            BlockPrivateAddresses = true
        });

        var decision = await allowlist.EvaluateAsync("127.0.0.1");

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("reserved");
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_RejectsPrivateRangeLiteral()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            BlockPrivateAddresses = true
        });

        var decision = await allowlist.EvaluateAsync("10.0.1.5");

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_RejectsCloudMetadataLiteral()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            BlockPrivateAddresses = true
        });

        var decision = await allowlist.EvaluateAsync("169.254.169.254");

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_RejectsLocalhostName()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            BlockPrivateAddresses = true
        });

        var decision = await allowlist.EvaluateAsync("localhost");

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_AllowsPublicLiteral()
    {
        var allowlist = Create(new ConnectionHostAllowlistOptions
        {
            BlockPrivateAddresses = true
        });

        var decision = await allowlist.EvaluateAsync("8.8.8.8");

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_RejectsHostResolvingToPrivateAddress()
    {
        var allowlist = Create(
            new ConnectionHostAllowlistOptions { BlockPrivateAddresses = true },
            resolver: _ => [IPAddress.Parse("192.168.10.10")]);

        var decision = await allowlist.EvaluateAsync("rebind.attacker.example");

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_AllowsHostResolvingToPublicAddress()
    {
        var allowlist = Create(
            new ConnectionHostAllowlistOptions { BlockPrivateAddresses = true },
            resolver: _ => [IPAddress.Parse("93.184.216.34")]);

        var decision = await allowlist.EvaluateAsync("public.example.com");

        decision.IsAllowed.Should().BeTrue();
    }

    [UnitTest]
    public async Task EvaluateAsync_WhenBlockingPrivate_RejectsUnresolvableHost_FailClosed()
    {
        var allowlist = Create(
            new ConnectionHostAllowlistOptions { BlockPrivateAddresses = true },
            resolver: _ => Array.Empty<IPAddress>());

        var decision = await allowlist.EvaluateAsync("nxdomain.invalid");

        decision.IsAllowed.Should().BeFalse();
    }

    [UnitTest]
    public async Task EvaluateAsync_CombinedPolicy_AllowlistedButPrivate_IsDenied()
    {
        // A host can be on the allowlist yet still resolve to a reserved address;
        // both controls apply, so the private-address block wins.
        var allowlist = Create(
            new ConnectionHostAllowlistOptions
            {
                AllowedHosts = ["db.internal.example.com"],
                BlockPrivateAddresses = true
            },
            resolver: _ => [IPAddress.Parse("10.1.2.3")]);

        var decision = await allowlist.EvaluateAsync("db.internal.example.com");

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("reserved");
    }
}
