// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance;
using Honua.Core.Features.Compliance.Services;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Compliance;

/// <summary>
/// Unit tests for <see cref="DefaultDataResidencyPolicyProvider"/> — covers the
/// "data residency controls can enforce configured regional boundaries"
/// acceptance criterion for #352.
/// </summary>
public sealed class DataResidencyPolicyTests
{
    [Fact]
    public void Disabled_Policy_IsInformationalOnly()
    {
        var provider = new DefaultDataResidencyPolicyProvider(
            new TestOptionsMonitor<ComplianceOptions>(new ComplianceOptions()));

        var decision = provider.Evaluate("eu-west-1");

        decision.Allowed.Should().BeTrue("informational-only policy must not block egress");
        decision.Reason.Should().Contain("informational only");
    }

    [Fact]
    public void Enforced_Policy_AllowsPrimaryRegionEvenWhenNotListed()
    {
        var opts = new ComplianceOptions
        {
            DataResidency = new ComplianceResidencyOptions
            {
                Enforced = true,
                PrimaryRegion = "us-gov-west-1",
                AllowedRegions = new List<string>(),
            },
        };

        var provider = new DefaultDataResidencyPolicyProvider(new TestOptionsMonitor<ComplianceOptions>(opts));
        var decision = provider.Evaluate("us-gov-west-1");

        decision.Allowed.Should().BeTrue("primary region must be implicitly allowed");
        decision.Policy.AllowedRegions.Should().Contain("us-gov-west-1");
    }

    [Fact]
    public void Enforced_Policy_DeniesUnlistedRegion()
    {
        var opts = new ComplianceOptions
        {
            DataResidency = new ComplianceResidencyOptions
            {
                Enforced = true,
                PrimaryRegion = "us-gov-west-1",
                AllowedRegions = new List<string> { "us-gov-east-1" },
            },
        };

        var provider = new DefaultDataResidencyPolicyProvider(new TestOptionsMonitor<ComplianceOptions>(opts));
        var decision = provider.Evaluate("eu-west-1");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("eu-west-1").And.Contain("not in the active residency allow-list");
    }

    [Fact]
    public void Empty_Region_Always_Denied()
    {
        var opts = new ComplianceOptions
        {
            DataResidency = new ComplianceResidencyOptions
            {
                Enforced = true,
                PrimaryRegion = "us-east-1",
            },
        };

        var provider = new DefaultDataResidencyPolicyProvider(new TestOptionsMonitor<ComplianceOptions>(opts));
        var decision = provider.Evaluate(string.Empty);

        decision.Allowed.Should().BeFalse("egress requires an explicit region");
    }

    [Fact]
    public void Allowed_Region_Match_IsCaseInsensitive()
    {
        var opts = new ComplianceOptions
        {
            DataResidency = new ComplianceResidencyOptions
            {
                Enforced = true,
                PrimaryRegion = "US-GOV-WEST-1",
                AllowedRegions = new List<string> { "us-gov-east-1" },
            },
        };

        var provider = new DefaultDataResidencyPolicyProvider(new TestOptionsMonitor<ComplianceOptions>(opts));

        provider.Evaluate("us-gov-west-1").Allowed.Should().BeTrue();
        provider.Evaluate("US-GOV-EAST-1").Allowed.Should().BeTrue();
    }

    [Fact]
    public void TopLevel_PrimaryRegion_Used_WhenNestedResidencyPrimaryRegionIsDefault()
    {
        // Operator sets only Compliance:PrimaryRegion (top-level) — the nested
        // DataResidency:PrimaryRegion is left at its default. The policy must
        // pick up the top-level value, not deny the operator-intended region.
        var opts = new ComplianceOptions
        {
            PrimaryRegion = "us-gov-west-1",
            DataResidency = new ComplianceResidencyOptions
            {
                Enforced = true,
                AllowedRegions = new List<string>(),
            },
        };

        var provider = new DefaultDataResidencyPolicyProvider(new TestOptionsMonitor<ComplianceOptions>(opts));
        var policy = provider.GetPolicy();

        policy.PrimaryRegion.Should().Be("us-gov-west-1");
        policy.AllowedRegions.Should().Contain("us-gov-west-1");
        provider.Evaluate("us-gov-west-1").Allowed.Should().BeTrue(
            "the operator-intended primary region must be implicitly allowed via the top-level fallback");
    }
}

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T current) => CurrentValue = current;
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
