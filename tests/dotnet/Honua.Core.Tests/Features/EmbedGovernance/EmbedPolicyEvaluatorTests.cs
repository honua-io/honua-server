// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.EmbedGovernance.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.EmbedGovernance;

/// <summary>
/// Unit tests for <see cref="EmbedPolicyEvaluator"/> covering origin/domain,
/// service, content, tenant, and rate-limit decisions.
/// </summary>
public sealed class EmbedPolicyEvaluatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 26, 0, 0, 0, TimeSpan.Zero);

    private static EmbedKeyRecord Key(EmbedKeyScope scope, DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        KeyPrefix = "embk_test",
        KeyHash = [1, 2, 3],
        Scope = scope,
        CreatedAt = _now.AddDays(-1),
        UpdatedAt = _now.AddDays(-1),
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt,
    };

    [UnitTest]
    public void Evaluate_AllowedOrigin_Allows()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["https://app.example.com"] });
        var request = new EmbedPolicyRequest { Origin = "https://app.example.com" };

        var decision = EmbedPolicyEvaluator.Evaluate(key, request, _now);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.None);
    }

    [UnitTest]
    public void Evaluate_OriginNotInList_DeniesOriginNotAllowed()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["https://app.example.com"] });
        var request = new EmbedPolicyRequest { Origin = "https://evil.example.org" };

        var decision = EmbedPolicyEvaluator.Evaluate(key, request, _now);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.OriginNotAllowed);
    }

    [UnitTest]
    public void Evaluate_EmptyOriginAllowList_DeniesEveryOrigin()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = [] });
        var request = new EmbedPolicyRequest { Origin = "https://app.example.com" };

        var decision = EmbedPolicyEvaluator.Evaluate(key, request, _now);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.OriginNotAllowed);
    }

    [UnitTest]
    public void Evaluate_SubdomainWildcard_MatchesSubdomain()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["*.example.com"] });

        var allowed = EmbedPolicyEvaluator.Evaluate(key, new EmbedPolicyRequest { Origin = "https://maps.example.com" }, _now);
        var denied = EmbedPolicyEvaluator.Evaluate(key, new EmbedPolicyRequest { Origin = "https://example.org" }, _now);

        allowed.Allowed.Should().BeTrue();
        denied.Allowed.Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_WildcardOrigin_AllowsAny()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["*"] });

        var decision = EmbedPolicyEvaluator.Evaluate(key, new EmbedPolicyRequest { Origin = "https://anything.test" }, _now);

        decision.Allowed.Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_OriginCaseAndTrailingSlash_Normalized()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["https://App.Example.com/"] });

        var decision = EmbedPolicyEvaluator.Evaluate(key, new EmbedPolicyRequest { Origin = "https://app.example.com" }, _now);

        decision.Allowed.Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_ServiceNotAllowed_Denies()
    {
        var key = Key(new EmbedKeyScope
        {
            AllowedEmbedOrigins = ["*"],
            AllowedServiceOrigins = ["services/Roads"],
        });

        var decision = EmbedPolicyEvaluator.Evaluate(
            key,
            new EmbedPolicyRequest { Origin = "https://app.test", ServiceId = "services/Secret" },
            _now);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.ServiceNotAllowed);
    }

    [UnitTest]
    public void Evaluate_EmptyServiceList_AllowsAnyService()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["*"] });

        var decision = EmbedPolicyEvaluator.Evaluate(
            key,
            new EmbedPolicyRequest { Origin = "https://app.test", ServiceId = "services/Anything" },
            _now);

        decision.Allowed.Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_ContentNotAllowed_Denies()
    {
        var key = Key(new EmbedKeyScope
        {
            AllowedEmbedOrigins = ["*"],
            AllowedContentIds = ["map-123"],
        });

        var decision = EmbedPolicyEvaluator.Evaluate(
            key,
            new EmbedPolicyRequest { Origin = "https://app.test", ContentId = "map-999" },
            _now);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.ContentNotAllowed);
    }

    [UnitTest]
    public void Evaluate_TenantMismatch_Denies()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["*"], TenantId = "acme" });

        var decision = EmbedPolicyEvaluator.Evaluate(
            key,
            new EmbedPolicyRequest { Origin = "https://app.test", TenantId = "globex" },
            _now);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.TenantMismatch);
    }

    [UnitTest]
    public void Evaluate_RateBudgetExceeded_DeniesRateLimited()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["*"], RateLimitRequestsPerWindow = 5 });

        var withinBudget = EmbedPolicyEvaluator.Evaluate(
            key,
            new EmbedPolicyRequest { Origin = "https://app.test", RequestsConsumedInWindow = 5 },
            _now);
        var overBudget = EmbedPolicyEvaluator.Evaluate(
            key,
            new EmbedPolicyRequest { Origin = "https://app.test", RequestsConsumedInWindow = 6 },
            _now);

        withinBudget.Allowed.Should().BeTrue();
        overBudget.Allowed.Should().BeFalse();
        overBudget.Reason.Should().Be(EmbedPolicyDenyReason.RateLimited);
    }

    [UnitTest]
    public void Evaluate_RevokedKey_DeniesKeyInactive()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["*"] }, revokedAt: _now.AddMinutes(-1));

        var decision = EmbedPolicyEvaluator.Evaluate(key, new EmbedPolicyRequest { Origin = "https://app.test" }, _now);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.KeyInactive);
    }

    [UnitTest]
    public void Evaluate_ExpiredKey_DeniesKeyInactive()
    {
        var key = Key(new EmbedKeyScope { AllowedEmbedOrigins = ["*"] }, expiresAt: _now.AddMinutes(-1));

        var decision = EmbedPolicyEvaluator.Evaluate(key, new EmbedPolicyRequest { Origin = "https://app.test" }, _now);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(EmbedPolicyDenyReason.KeyInactive);
    }

    [UnitTest]
    public void BuildPolicy_ProjectsScopeAndRateLimit()
    {
        var key = Key(new EmbedKeyScope
        {
            AllowedEmbedOrigins = ["https://app.example.com"],
            AllowedServiceOrigins = ["services/Roads"],
            AllowedContentIds = ["map-1"],
            IntegrationId = "site-7",
            TenantId = "acme",
            Edition = "pro",
            RateLimitRequestsPerWindow = 100,
            RateLimitWindow = TimeSpan.FromSeconds(30),
        });

        var policy = EmbedPolicyEvaluator.BuildPolicy(key);

        policy.AllowedOrigins.Should().ContainSingle().Which.Should().Be("https://app.example.com");
        policy.IntegrationId.Should().Be("site-7");
        policy.TenantId.Should().Be("acme");
        policy.Edition.Should().Be("pro");
        policy.RateLimit.RequestsPerWindow.Should().Be(100);
        policy.RateLimit.WindowSeconds.Should().Be(30);
        policy.Capabilities.Should().NotBeEmpty();
    }
}
