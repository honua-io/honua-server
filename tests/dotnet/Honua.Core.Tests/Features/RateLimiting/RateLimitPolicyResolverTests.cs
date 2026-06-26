// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.RateLimiting.Domain;

namespace Honua.Core.Tests.Features.RateLimiting;

/// <summary>
/// Unit tests for the tier-precedence resolver (issue #2158): an explicit per-API-key quota
/// overrides a per-tenant quota, which overrides a per-plan quota.
/// </summary>
public sealed class RateLimitPolicyResolverTests
{
    private static RateLimitPolicy Policy(string scope, string key, int limit = 100) => new()
    {
        Name = $"{scope}:{key}",
        Scope = scope,
        Key = key,
        RequestsPerWindow = limit,
        WindowDuration = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public void Resolve_ApiKeyTier_WinsOverTenantAndPlan()
    {
        var policies = new[]
        {
            Policy(RateLimitScopes.Plan, "free", 10),
            Policy(RateLimitScopes.Tenant, "acme", 100),
            Policy(RateLimitScopes.ApiKey, "key-1", 1000),
        };

        var resolved = RateLimitPolicyResolver.Resolve(policies, new RateLimitRequestDescriptor
        {
            ApiKey = "key-1",
            TenantId = "acme",
            Plan = "free",
        });

        resolved.Should().NotBeNull();
        resolved!.Scope.Should().Be(RateLimitScopes.ApiKey);
        resolved.RequestsPerWindow.Should().Be(1000);
    }

    [Fact]
    public void Resolve_TenantTier_WinsOverPlan_WhenNoApiKeyPolicy()
    {
        var policies = new[]
        {
            Policy(RateLimitScopes.Plan, "free", 10),
            Policy(RateLimitScopes.Tenant, "acme", 100),
        };

        var resolved = RateLimitPolicyResolver.Resolve(policies, new RateLimitRequestDescriptor
        {
            ApiKey = "key-1",
            TenantId = "acme",
            Plan = "free",
        });

        resolved!.Scope.Should().Be(RateLimitScopes.Tenant);
    }

    [Fact]
    public void Resolve_PlanTier_WinsWhenOnlyPlanMatches()
    {
        var policies = new[] { Policy(RateLimitScopes.Plan, "free", 10) };

        var resolved = RateLimitPolicyResolver.Resolve(policies, new RateLimitRequestDescriptor
        {
            TenantId = "acme",
            Plan = "free",
        });

        resolved!.Scope.Should().Be(RateLimitScopes.Plan);
    }

    [Fact]
    public void Resolve_DisabledMostSpecificPolicy_FallsThroughToNextTier()
    {
        var apiKeyPolicy = new RateLimitPolicy
        {
            Name = "key",
            Scope = RateLimitScopes.ApiKey,
            Key = "key-1",
            RequestsPerWindow = 1000,
            WindowDuration = TimeSpan.FromMinutes(1),
            Enabled = false,
        };

        var policies = new[] { apiKeyPolicy, Policy(RateLimitScopes.Tenant, "acme", 100) };

        var resolved = RateLimitPolicyResolver.Resolve(policies, new RateLimitRequestDescriptor
        {
            ApiKey = "key-1",
            TenantId = "acme",
        });

        resolved!.Scope.Should().Be(RateLimitScopes.Tenant);
    }

    [Fact]
    public void Resolve_NoMatchingTier_ReturnsNull()
    {
        var policies = new[] { Policy(RateLimitScopes.Tenant, "other", 100) };

        var resolved = RateLimitPolicyResolver.Resolve(policies, new RateLimitRequestDescriptor
        {
            TenantId = "acme",
            Plan = "free",
        });

        resolved.Should().BeNull();
    }

    [Fact]
    public void Resolve_KeyMatch_IsCaseInsensitive()
    {
        var policies = new[] { Policy(RateLimitScopes.Tenant, "Acme", 100) };

        var resolved = RateLimitPolicyResolver.Resolve(policies, new RateLimitRequestDescriptor
        {
            TenantId = "acme",
        });

        resolved!.Scope.Should().Be(RateLimitScopes.Tenant);
    }

    [Fact]
    public void IsKnown_RecognisesAllTiersAndRejectsOthers()
    {
        RateLimitScopes.IsKnown(RateLimitScopes.Plan).Should().BeTrue();
        RateLimitScopes.IsKnown(RateLimitScopes.Tenant).Should().BeTrue();
        RateLimitScopes.IsKnown(RateLimitScopes.ApiKey).Should().BeTrue();
        RateLimitScopes.IsKnown(RateLimitScopes.Endpoint).Should().BeTrue();
        RateLimitScopes.IsKnown("API-KEY").Should().BeTrue();
        RateLimitScopes.IsKnown("bogus").Should().BeFalse();
        RateLimitScopes.IsKnown(null).Should().BeFalse();
    }
}
