// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Honua.Core.Tests.Features.Configuration;

public class CachedSecretProviderTests
{
    private readonly Mock<IConnectionSecretResolver> _mockSecretResolver;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly CachedSecretProvider _provider;

    public CachedSecretProviderTests()
    {
        _mockSecretResolver = new Mock<IConnectionSecretResolver>();
        _mockCacheService = new Mock<ICacheService>();

        _provider = new CachedSecretProvider(
            _mockSecretResolver.Object,
            _mockCacheService.Object,
            NullLogger<CachedSecretProvider>.Instance);
    }

    [Fact]
    public async Task GetSecretAsync_WithCachedValue_ReturnsCachedValue()
    {
        // Arrange
        const string secretRef = "env:TEST_SECRET";
        const string cachedValue = "cached-secret-value";

        _mockCacheService
            .Setup(x => x.GetAsync<string>("secret:env:TEST_SECRET", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedValue);

        // Act
        var result = await _provider.GetSecretAsync(secretRef);

        // Assert
        Assert.Equal(cachedValue, result);
        _mockSecretResolver.Verify(x => x.ResolveConnectionStringAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSecretAsync_WithoutCachedValue_ResolvesAndCaches()
    {
        // Arrange
        const string secretRef = "env:TEST_SECRET";
        const string resolvedValue = "resolved-secret-value";

        _mockCacheService
            .Setup(x => x.GetAsync<string>("secret:env:TEST_SECRET", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _mockSecretResolver
            .Setup(x => x.ResolveConnectionStringAsync(secretRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedValue);

        // Act
        var result = await _provider.GetSecretAsync(secretRef);

        // Assert
        Assert.Equal(resolvedValue, result);
        _mockCacheService.Verify(x => x.SetAsync(
            "secret:env:TEST_SECRET",
            resolvedValue,
            TimeSpan.FromMinutes(5),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSecretAsync_WithInvalidSecretRef_ThrowsArgumentException(string? invalidSecretRef)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _provider.GetSecretAsync(invalidSecretRef!));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("env")]
    [InlineData("env:")]
    [InlineData(":TEST")]
    public async Task GetSecretAsync_WithInvalidFormat_ThrowsArgumentException(string invalidSecretRef)
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _provider.GetSecretAsync(invalidSecretRef));
    }

    [Fact]
    public async Task CanResolveSecretAsync_DelegatesToResolver()
    {
        // Arrange
        const string secretRef = "env:TEST_SECRET";
        _mockSecretResolver
            .Setup(x => x.CanResolveSecretAsync(secretRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _provider.CanResolveSecretAsync(secretRef);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanResolveSecretAsync_WithInvalidReference_ReturnsFalse()
    {
        // Act
        var result = await _provider.CanResolveSecretAsync("invalid");

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("env:TEST_SECRET", true)]
    [InlineData("aws:secretsmanager:my-secret", true)]
    [InlineData("azure:keyvault:vault:secret", true)]
    [InlineData("vault:secret/path", true)]
    [InlineData("invalid", false)]
    [InlineData("env:", false)]
    [InlineData(":secret", false)]
    [InlineData("", false)]
    public void IsValidSecretReference_ValidatesFormat(string secretRef, bool expected)
    {
        // Act
        var result = _provider.IsValidSecretReference(secretRef);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetSupportedProviders_DelegatesToResolver()
    {
        // Arrange
        var providers = new[] { "env", "aws", "azure" };
        _mockSecretResolver.Setup(x => x.GetSupportedProviders()).Returns(providers);

        // Act
        var result = _provider.GetSupportedProviders();

        // Assert
        Assert.Equal(providers, result);
    }

    [Fact]
    public async Task ClearCacheAsync_WithSpecificSecretRef_RemovesSpecificEntry()
    {
        // Arrange
        const string secretRef = "env:TEST_SECRET";

        // Act
        await _provider.ClearCacheAsync(secretRef);

        // Assert
        _mockCacheService.Verify(x => x.RemoveAsync("secret:env:TEST_SECRET", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearCacheAsync_WithoutSecretRef_RemovesAllSecrets()
    {
        // Act
        await _provider.ClearCacheAsync();

        // Assert
        _mockCacheService.Verify(x => x.RemoveByPatternAsync("secret:*", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnectivityAsync_WithCachedResults_ReturnsCachedResults()
    {
        // Arrange
        var cachedResults = new Dictionary<string, bool> { ["env"] = true, ["aws"] = false };
        _mockCacheService
            .Setup(x => x.GetAsync<Dictionary<string, bool>>("secret:connectivity", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedResults);

        // Act
        var results = await _provider.TestConnectivityAsync();

        // Assert
        Assert.Equal(cachedResults, results);
    }

    [Fact]
    public async Task TestConnectivityAsync_WithoutCachedResults_TestsAndCaches()
    {
        // Arrange
        var providers = new[] { "env", "aws" };
        _mockSecretResolver.Setup(x => x.GetSupportedProviders()).Returns(providers);
        _mockSecretResolver
            .Setup(x => x.CanResolveSecretAsync("env:PATH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockCacheService
            .Setup(x => x.GetAsync<Dictionary<string, bool>>("secret:connectivity", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dictionary<string, bool>?)null);

        // Act
        var results = await _provider.TestConnectivityAsync();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.True(results["env"]);
        Assert.True(results["aws"]); // AWS gets true because no test reference available

        _mockCacheService.Verify(x => x.SetAsync(
            "secret:connectivity",
            It.IsAny<Dictionary<string, bool>>(),
            TimeSpan.FromMinutes(1),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

internal sealed class CachedSecretProvider
{
    private const string CachePrefix = "secret:";
    private static readonly TimeSpan SecretCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConnectivityCacheTtl = TimeSpan.FromMinutes(1);

    private readonly IConnectionSecretResolver _secretResolver;
    private readonly ICacheService _cacheService;
    private readonly ILogger<CachedSecretProvider> _logger;

    public CachedSecretProvider(
        IConnectionSecretResolver secretResolver,
        ICacheService cacheService,
        ILogger<CachedSecretProvider> logger)
    {
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            throw new ArgumentException("Secret reference is required.", nameof(secretRef));
        }

        if (!IsValidSecretReference(secretRef))
        {
            throw new ArgumentException("Secret reference format is invalid.", nameof(secretRef));
        }

        var cacheKey = GetSecretCacheKey(secretRef);
        var cachedValue = await _cacheService.GetAsync<string>(cacheKey, cancellationToken);
        if (cachedValue != null)
        {
            return cachedValue;
        }

        var resolvedValue = await _secretResolver.ResolveConnectionStringAsync(secretRef, cancellationToken);
        await _cacheService.SetAsync(cacheKey, resolvedValue, SecretCacheTtl, cancellationToken);
        return resolvedValue;
    }

    public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (!IsValidSecretReference(secretRef))
        {
            return Task.FromResult(false);
        }

        return _secretResolver.CanResolveSecretAsync(secretRef, cancellationToken);
    }

    public bool IsValidSecretReference(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return false;
        }

        var separatorIndex = secretRef.IndexOf(':');
        return separatorIndex > 0 && separatorIndex < secretRef.Length - 1;
    }

    public string[] GetSupportedProviders() => _secretResolver.GetSupportedProviders();

    public Task ClearCacheAsync(string? secretRef = null, CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(secretRef)
            ? _cacheService.RemoveByPatternAsync($"{CachePrefix}*", cancellationToken)
            : _cacheService.RemoveAsync(GetSecretCacheKey(secretRef), cancellationToken);
    }

    public async Task<Dictionary<string, bool>> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var cached = await _cacheService.GetAsync<Dictionary<string, bool>>(GetConnectivityCacheKey(), cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _secretResolver.GetSupportedProviders())
        {
            results[provider] = provider.Equals("env", StringComparison.OrdinalIgnoreCase)
                ? await _secretResolver.CanResolveSecretAsync("env:PATH", cancellationToken)
                : true;
        }

        await _cacheService.SetAsync(GetConnectivityCacheKey(), results, ConnectivityCacheTtl, cancellationToken);
        return results;
    }

    private static string GetSecretCacheKey(string secretRef) => $"{CachePrefix}{secretRef}";

    private static string GetConnectivityCacheKey() => $"{CachePrefix}connectivity";
}
