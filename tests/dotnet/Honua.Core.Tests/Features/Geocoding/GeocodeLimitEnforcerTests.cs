// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Services;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Geocoding;

public sealed class GeocodeLimitEnforcerTests
{
    private static GeocodeProviderCapabilities Caps(int? rateLimit = null, int maxBatch = 100)
        => new() { RateLimitPerMinute = rateLimit, MaxBatchSize = maxBatch };

    [Fact]
    public void CheckRequestRate_WithinLimit_Allows()
    {
        var enforcer = new GeocodeLimitEnforcer(Options.Create(new GeocodingConfiguration()));
        var caps = Caps(rateLimit: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(enforcer.CheckRequestRate("azure-maps", caps).Allowed);
        }
    }

    [Fact]
    public void CheckRequestRate_OverLimit_RejectsWithRetryAfter()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var enforcer = new GeocodeLimitEnforcer(Options.Create(new GeocodingConfiguration()), time);
        var caps = Caps(rateLimit: 2);

        Assert.True(enforcer.CheckRequestRate("azure-maps", caps).Allowed);
        Assert.True(enforcer.CheckRequestRate("azure-maps", caps).Allowed);

        var rejected = enforcer.CheckRequestRate("azure-maps", caps);

        Assert.False(rejected.Allowed);
        Assert.Equal(GeocodeLimitKind.RateLimit, rejected.Kind);
        Assert.Equal(2, rejected.EffectiveLimit);
        Assert.NotNull(rejected.RetryAfter);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void CheckRequestRate_WindowResets_AllowsAgain()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var enforcer = new GeocodeLimitEnforcer(Options.Create(new GeocodingConfiguration()), time);
        var caps = Caps(rateLimit: 1);

        Assert.True(enforcer.CheckRequestRate("amazon-location", caps).Allowed);
        Assert.False(enforcer.CheckRequestRate("amazon-location", caps).Allowed);

        // Advance past the one-minute fixed window.
        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        Assert.True(enforcer.CheckRequestRate("amazon-location", caps).Allowed);
    }

    [Fact]
    public void CheckRequestRate_ScopesAreIndependent()
    {
        var enforcer = new GeocodeLimitEnforcer(Options.Create(new GeocodingConfiguration()));
        var caps = Caps(rateLimit: 1);

        Assert.True(enforcer.CheckRequestRate("azure-maps", caps, scope: "tenant-a").Allowed);
        // A different scope has its own window.
        Assert.True(enforcer.CheckRequestRate("azure-maps", caps, scope: "tenant-b").Allowed);
        // The first scope is now exhausted.
        Assert.False(enforcer.CheckRequestRate("azure-maps", caps, scope: "tenant-a").Allowed);
    }

    [Fact]
    public void CheckRequestRate_NoAdvertisedLimit_AlwaysAllows()
    {
        var enforcer = new GeocodeLimitEnforcer(Options.Create(new GeocodingConfiguration()));
        var caps = Caps(rateLimit: null);

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(enforcer.CheckRequestRate("nominatim", caps).Allowed);
        }
    }

    [Fact]
    public void CheckRequestRate_EnforcementDisabled_AlwaysAllows()
    {
        var config = new GeocodingConfiguration { EnforceRateLimits = false };
        var enforcer = new GeocodeLimitEnforcer(Options.Create(config));
        var caps = Caps(rateLimit: 1);

        Assert.True(enforcer.CheckRequestRate("azure-maps", caps).Allowed);
        Assert.True(enforcer.CheckRequestRate("azure-maps", caps).Allowed);
    }

    [Fact]
    public void CheckBatch_WithinProviderCap_Allows()
    {
        var enforcer = new GeocodeLimitEnforcer(Options.Create(new GeocodingConfiguration()));

        var decision = enforcer.CheckBatch("nominatim", Caps(maxBatch: 100), requestedBatchSize: 50);

        Assert.True(decision.Allowed);
        Assert.Equal(100, decision.EffectiveLimit);
    }

    [Fact]
    public void CheckBatch_OverProviderCap_Rejects()
    {
        var enforcer = new GeocodeLimitEnforcer(Options.Create(new GeocodingConfiguration()));

        var decision = enforcer.CheckBatch("nominatim", Caps(maxBatch: 100), requestedBatchSize: 101);

        Assert.False(decision.Allowed);
        Assert.Equal(GeocodeLimitKind.BatchSize, decision.Kind);
        Assert.Equal(100, decision.EffectiveLimit);
        Assert.Contains(GeocodeLimitMetadata.BatchSizeMarker, decision.Reason);
    }

    [Fact]
    public void CheckBatch_LicenseCapLowerThanProvider_UsesLicenseCap()
    {
        var config = new GeocodingConfiguration { MaxBatchSizeLimit = 25 };
        var enforcer = new GeocodeLimitEnforcer(Options.Create(config));

        var allowed = enforcer.CheckBatch("nominatim", Caps(maxBatch: 100), requestedBatchSize: 25);
        var rejected = enforcer.CheckBatch("nominatim", Caps(maxBatch: 100), requestedBatchSize: 26);

        Assert.True(allowed.Allowed);
        Assert.Equal(25, allowed.EffectiveLimit);
        Assert.False(rejected.Allowed);
        Assert.Equal(25, rejected.EffectiveLimit);
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
