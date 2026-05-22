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
    public void Whitespace_Padded_PrimaryRegion_IsTrimmed_AndStillImplicitlyAllowed()
    {
        // Operator config carries a stray-whitespace primary region (e.g. from a
        // YAML-multiline or copy-paste). NormalizeAllowedRegions already trims
        // configured entries; the primary region must get the same treatment so
        // Evaluate("us-gov-west-1") (un-padded) still matches the implicitly-allowed
        // primary instead of denying.
        var opts = new ComplianceOptions
        {
            DataResidency = new ComplianceResidencyOptions
            {
                Enforced = true,
                PrimaryRegion = "  us-gov-west-1  ",
                AllowedRegions = new List<string>(),
            },
        };

        var provider = new DefaultDataResidencyPolicyProvider(new TestOptionsMonitor<ComplianceOptions>(opts));
        var policy = provider.GetPolicy();

        policy.PrimaryRegion.Should().Be("us-gov-west-1", "the primary region must be trimmed before storage");
        policy.AllowedRegions.Should().Contain("us-gov-west-1");
        provider.Evaluate("us-gov-west-1").Allowed.Should().BeTrue(
            "the trimmed primary region must remain implicitly allowed for un-padded callers");
    }

    [Fact]
    public void Whitespace_Padded_EvaluatedRegion_IsTrimmed_BeforeComparison()
    {
        // Sibling case: a caller (admin dry-run, future egress guard) supplies a
        // whitespace-padded region. The decision and the reason message must use
        // the trimmed form so the verdict matches the allow-list semantics.
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

        var primaryDecision = provider.Evaluate("  us-gov-west-1  ");
        primaryDecision.Allowed.Should().BeTrue("padded primary region must match the trimmed allow-list entry");
        primaryDecision.Region.Should().Be("us-gov-west-1", "the decision must report the trimmed region");

        var allowedDecision = provider.Evaluate(" us-gov-east-1 ");
        allowedDecision.Allowed.Should().BeTrue("padded allow-list region must match");
        allowedDecision.Region.Should().Be("us-gov-east-1");

        var deniedDecision = provider.Evaluate(" eu-west-1 ");
        deniedDecision.Allowed.Should().BeFalse();
        deniedDecision.Reason.Should().Contain("eu-west-1");
        deniedDecision.Reason.Should().NotContain(" eu-west-1 ", "the reason must use the trimmed region");
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
