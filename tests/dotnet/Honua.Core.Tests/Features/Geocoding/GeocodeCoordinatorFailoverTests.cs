// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geocoding.Features.Geocoding;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Core.Tests.Features.Geocoding;

public sealed class GeocodeCoordinatorFailoverTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void AddGeocodingCore_WithoutLicenseService_ResolvesProviderCoordinator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGeocodingCore(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IGeocodeProviderCoordinator>());
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ForwardGeocodeAsync_CapabilityIncompatibleFirstProvider_DoesNotConsumeFailoverBudget()
    {
        // The first (default) provider cannot forward-geocode; a later provider can. The
        // capability skip must not consume the MaxFailoverAttempts budget, so the capable
        // provider is still attempted and succeeds.
        var incapable = new FakeGeocodeProvider(
            "incapable",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: false));

        var capable = new FakeGeocodeProvider(
            "capable",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: true))
        {
            ForwardResult =
            [
                new GeocodeCandidate("1 Test St", 1.0, 2.0, 99.0, new Dictionary<string, string?>())
            ]
        };

        var coordinator = CreateCoordinator(
            providers: [incapable, capable],
            defaultProvider: "incapable",
            maxFailoverAttempts: 1);

        var result = await coordinator.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("1 Test St", 1, 4326, null),
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("capable", result.ProviderName);
        Assert.Single(result.Data);
        Assert.Equal(1, capable.ForwardCallCount);
        Assert.Equal(0, incapable.ForwardCallCount);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ForwardGeocodeAsync_ExceedsAdvertisedRateLimit_ThrottlesWithRetryAfter()
    {
        // The provider advertises one request per minute; the second request in the window must be
        // throttled before it reaches the provider, carrying a Retry-After hint (#2150).
        var provider = new FakeGeocodeProvider(
            "capable",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: true) { RateLimitPerMinute = 1 })
        {
            ForwardResult =
            [
                new GeocodeCandidate("1 Test St", 1.0, 2.0, 99.0, new Dictionary<string, string?>())
            ]
        };

        var coordinator = CreateCoordinator(
            providers: [provider],
            defaultProvider: "capable",
            maxFailoverAttempts: 1);

        var request = new ForwardGeocodeRequest("1 Test St", 1, 4326, null);

        var first = await coordinator.ForwardGeocodeAsync(
            request,
            providerName: null,
            allowFailover: true,
            CancellationToken.None);
        var second = await coordinator.ForwardGeocodeAsync(
            request,
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Contains(GeocodeLimitMetadata.RateLimitMarker, second.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(second.Metadata);
        Assert.True(second.Metadata!.ContainsKey(GeocodeLimitMetadata.RetryAfterSecondsKey));

        // The throttled request must never reach the provider.
        Assert.Equal(1, provider.ForwardCallCount);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ForwardGeocodeAsync_AllProvidersLackCapability_FailsWithNoProviderSupportsMessage()
    {
        var incapableA = new FakeGeocodeProvider(
            "a",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: false));
        var incapableB = new FakeGeocodeProvider(
            "b",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: false));

        var coordinator = CreateCoordinator(
            providers: [incapableA, incapableB],
            defaultProvider: "a",
            maxFailoverAttempts: 3);

        var result = await coordinator.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("1 Test St", 1, 4326, null),
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("No provider supports", result.ErrorMessage);
        Assert.Empty(result.AttemptedProviders ?? []);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReverseGeocodeAsync_CapabilityIncompatibleFirstProvider_FailsOverToCapableProvider()
    {
        var incapable = new FakeGeocodeProvider(
            "incapable",
            new GeocodeProviderCapabilities(SupportsReverseGeocode: false));

        var capable = new FakeGeocodeProvider(
            "capable",
            new GeocodeProviderCapabilities(SupportsReverseGeocode: true))
        {
            ReverseResult = new ReverseGeocodeMatch("1 Test St", 1.0, 2.0, new Dictionary<string, string?>())
        };

        var coordinator = CreateCoordinator(
            providers: [incapable, capable],
            defaultProvider: "incapable",
            maxFailoverAttempts: 1);

        var result = await coordinator.ReverseGeocodeAsync(
            new ReverseGeocodeRequest(1.0, 2.0, 4326),
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("capable", result.ProviderName);
        Assert.NotNull(result.Data);
        Assert.Equal(1, capable.ReverseCallCount);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ForwardGeocodeAsync_FailoverBudgetCapsRealAttempts()
    {
        // Two erroring providers with a budget of one: only the first should actually attempt.
        var first = new FakeGeocodeProvider(
            "first",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: true))
        {
            ForwardException = new GeocodeProviderException("first failed")
        };
        var second = new FakeGeocodeProvider(
            "second",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: true))
        {
            ForwardException = new GeocodeProviderException("second failed")
        };

        var coordinator = CreateCoordinator(
            providers: [first, second],
            defaultProvider: "first",
            maxFailoverAttempts: 1);

        var result = await coordinator.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("1 Test St", 1, 4326, null),
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, first.ForwardCallCount);
        Assert.Equal(0, second.ForwardCallCount);
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeocodeProviderCoordinator_Operations_PassFailoverEntitlementToCanonicalCoordinator(
        bool isActive)
    {
        var forwardRequest = new ForwardGeocodeRequest("1 Test St", 1, 4326, null);
        var reverseRequest = new ReverseGeocodeRequest(1.0, 2.0, 4326);
        var canonical = new Mock<IGeocodeCoordinatorService>(MockBehavior.Strict);
        canonical
            .Setup(service => service.ForwardGeocodeAsync(
                forwardRequest,
                null,
                isActive,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeocodeResult<IReadOnlyList<GeocodeCandidate>>
            {
                Data = [],
                ProviderName = "mock"
            });
        canonical
            .Setup(service => service.ReverseGeocodeAsync(
                reverseRequest,
                null,
                isActive,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeocodeResult<ReverseGeocodeMatch?>
            {
                Data = null,
                ProviderName = "mock"
            });

        var entitlement = new Mock<ILicenseEntitlementService>(MockBehavior.Strict);
        entitlement
            .Setup(service => service.CheckEntitlement(FeatureCatalog.GeocodingFailoverKey))
            .Returns(new LicenseEntitlementDecision(
                FeatureCatalog.GeocodingFailoverKey,
                isActive,
                isActive ? HonuaEdition.Pro : HonuaEdition.Community,
                isActive ? LicenseValidationState.Valid : LicenseValidationState.NoLicenseConfigured,
                HonuaEdition.Pro,
                isActive ? string.Empty : "Failover requires Pro."));

        var coordinator = new GeocodeProviderCoordinator(
            canonical.Object,
            Mock.Of<IGeocodeProviderRegistry>(),
            entitlement.Object);

        await coordinator.ForwardGeocodeAsync(forwardRequest, cancellationToken: CancellationToken.None);
        await coordinator.ReverseGeocodeAsync(reverseRequest, cancellationToken: CancellationToken.None);

        canonical.VerifyAll();
        entitlement.Verify(
            service => service.CheckEntitlement(FeatureCatalog.GeocodingFailoverKey),
            Times.Exactly(2));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task BatchGeocodeAsync_FailoverTargetOverItsCap_DoesNotReceiveTheBatch()
    {
        var primary = new FakeGeocodeProvider(
            "primary",
            new GeocodeProviderCapabilities(SupportsBatch: true) { MaxBatchSize = 1000 })
        {
            BatchException = new InvalidOperationException("primary unavailable"),
        };
        var secondary = new FakeGeocodeProvider(
            "secondary",
            new GeocodeProviderCapabilities(SupportsBatch: true) { MaxBatchSize = 100 });

        var coordinator = CreateCoordinator(
            providers: [primary, secondary],
            defaultProvider: "primary",
            maxFailoverAttempts: 2);

        var queries = Enumerable.Range(0, 500).Select(index => $"address {index}").ToArray();
        // Unnamed requests may still fail over. An explicit provider name must not (#6601).
        var result = await coordinator.BatchGeocodeAsync(
            new BatchGeocodeRequest(queries),
            providerName: null,
            allowFailover: true,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(GeocodeLimitMetadata.BatchSizeMarker, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, primary.BatchCallCount);
        Assert.Equal(0, secondary.BatchCallCount);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ForwardGeocodeAsync_ExplicitProviderMissing_DoesNotCallNominatim()
    {
        var nominatim = new FakeGeocodeProvider(
            "nominatim",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: true))
        {
            ForwardResult =
            [
                new GeocodeCandidate("leaked", 1.0, 2.0, 99.0, new Dictionary<string, string?>())
            ]
        };

        var coordinator = CreateCoordinator(
            providers: [nominatim],
            defaultProvider: "nominatim",
            maxFailoverAttempts: 3);

        var result = await coordinator.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("1 Secret St", 1, 4326, null),
            providerName: "local",
            allowFailover: true,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("provider_unavailable", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("local", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, nominatim.ForwardCallCount);
        Assert.Equal(GeocodeErrorCodes.ProviderUnavailable, result.Metadata?["errorCode"]);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ForwardGeocodeAsync_ExplicitProviderDown_DoesNotCallNominatim()
    {
        var local = new FakeGeocodeProvider(
            "local",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: true))
        {
            ForwardException = new GeocodeProviderException("local geocoder is down")
        };
        var nominatim = new FakeGeocodeProvider(
            "nominatim",
            new GeocodeProviderCapabilities(SupportsForwardGeocode: true))
        {
            ForwardResult =
            [
                new GeocodeCandidate("leaked", 1.0, 2.0, 99.0, new Dictionary<string, string?>())
            ]
        };

        var coordinator = CreateCoordinator(
            providers: [local, nominatim],
            defaultProvider: "nominatim",
            maxFailoverAttempts: 3);

        var result = await coordinator.ForwardGeocodeAsync(
            new ForwardGeocodeRequest("1 Secret St", 1, 4326, null),
            providerName: "local",
            allowFailover: true,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("nominatim", result.ProviderName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, local.ForwardCallCount);
        Assert.Equal(0, nominatim.ForwardCallCount);
    }

    private static GeocodeCoordinatorService CreateCoordinator(
        IReadOnlyList<IGeocodeProvider> providers,
        string defaultProvider,
        int maxFailoverAttempts)
    {
        var configuration = new GeocodingConfiguration
        {
            DefaultProvider = defaultProvider,
            EnableFailover = true,
            MaxFailoverAttempts = maxFailoverAttempts
        };

        var registry = new GeocodeProviderRegistry(
            new EmptyServiceProvider(),
            providers,
            Options.Create(configuration));

        var limitEnforcer = new GeocodeLimitEnforcer(Options.Create(configuration));

        return new GeocodeCoordinatorService(
            registry,
            Options.Create(configuration),
            limitEnforcer,
            NullLogger<GeocodeCoordinatorService>.Instance);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeGeocodeProvider : IGeocodeProvider
    {
        public FakeGeocodeProvider(string name, GeocodeProviderCapabilities capabilities)
        {
            Name = name;
            Capabilities = capabilities;
        }

        public string Name { get; }

        public GeocodeProviderCapabilities Capabilities { get; }

        public IReadOnlyList<GeocodeCandidate> ForwardResult { get; init; } = [];

        public ReverseGeocodeMatch? ReverseResult { get; init; }

        public Exception? ForwardException { get; init; }

        public Exception? BatchException { get; init; }

        public int ForwardCallCount { get; private set; }

        public int ReverseCallCount { get; private set; }

        public int BatchCallCount { get; private set; }

        public Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
            ForwardGeocodeRequest request,
            CancellationToken cancellationToken = default)
        {
            ForwardCallCount++;

            if (ForwardException is not null)
            {
                throw ForwardException;
            }

            return Task.FromResult(ForwardResult);
        }

        public Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
            ReverseGeocodeRequest request,
            CancellationToken cancellationToken = default)
        {
            ReverseCallCount++;
            return Task.FromResult(ReverseResult);
        }

        public Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(
            SuggestGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GeocodeSuggestion>>([]);

        public Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(
            BatchGeocodeRequest request,
            CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            if (BatchException is not null)
            {
                throw BatchException;
            }

            return Task.FromResult<IReadOnlyList<GeocodeCandidate>>([]);
        }

        public Task<GeocodeProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GeocodeProviderHealth(Name, true, LastChecked: DateTime.UtcNow));
    }
}
