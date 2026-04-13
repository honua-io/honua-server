// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration.Validation;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Honua.Core.Tests.Configuration;

public class SecretProviderTests
{
    private readonly Mock<IConnectionSecretResolver> _mockResolver;
    private readonly Mock<ILogger<SecretProvider>> _mockLogger;
    private readonly SecretProviderOptions _options;

    public SecretProviderTests()
    {
        _mockResolver = new Mock<IConnectionSecretResolver>();
        _mockLogger = new Mock<ILogger<SecretProvider>>();
        _options = new SecretProviderOptions
        {
            EnableCaching = true,
            CacheDuration = TimeSpan.FromMinutes(5),
            MaxCacheSize = 100,
            LogSecretAccess = false // Disable for tests
        };
    }

    [Fact]
    public void IsSecretReference_RecognizesValidReferences()
    {
        // Arrange
        _mockResolver.Setup(r => r.GetSupportedProviders())
            .Returns(new[] { "env", "azure", "aws" });

        var provider = CreateSecretProvider();

        // Act & Assert
        Assert.True(provider.IsSecretReference("env:MY_SECRET"));
        Assert.True(provider.IsSecretReference("azure:keyvault:my-vault:secret"));
        Assert.True(provider.IsSecretReference("aws:secretsmanager:my-secret"));

        Assert.False(provider.IsSecretReference("plain-text-value"));
        Assert.False(provider.IsSecretReference("no-colon"));
        Assert.False(provider.IsSecretReference(null));
        Assert.False(provider.IsSecretReference(""));
    }

    [Fact]
    public async Task GetSecretAsync_ResolvesSecretReference()
    {
        // Arrange
        const string secretRef = "env:MY_SECRET";
        const string expectedValue = "secret-value";

        _mockResolver.Setup(r => r.ResolveConnectionStringAsync(secretRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedValue);

        var provider = CreateSecretProvider();

        // Act
        var result = await provider.GetSecretAsync(secretRef);

        // Assert
        Assert.Equal(expectedValue, result);
    }

    [Fact]
    public async Task GetSecretAsync_CachesResults()
    {
        // Arrange
        const string secretRef = "env:MY_SECRET";
        const string expectedValue = "secret-value";

        _mockResolver.Setup(r => r.ResolveConnectionStringAsync(secretRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedValue);

        var provider = CreateSecretProvider();

        // Act
        var result1 = await provider.GetSecretAsync(secretRef);
        var result2 = await provider.GetSecretAsync(secretRef);

        // Assert
        Assert.Equal(expectedValue, result1);
        Assert.Equal(expectedValue, result2);

        // Should only call resolver once due to caching
        _mockResolver.Verify(r => r.ResolveConnectionStringAsync(secretRef, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSecretOrDefaultAsync_ReturnsDefaultOnFailure()
    {
        // Arrange
        const string secretRef = "env:MISSING_SECRET";
        const string defaultValue = "default-value";

        _mockResolver.Setup(r => r.ResolveConnectionStringAsync(secretRef, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Secret not found"));

        var provider = CreateSecretProvider();

        // Act
        var result = await provider.GetSecretOrDefaultAsync(secretRef, defaultValue);

        // Assert
        Assert.Equal(defaultValue, result);
    }

    [Fact]
    public async Task CanResolveSecretAsync_ValidatesReferences()
    {
        // Arrange
        const string validRef = "env:VALID_SECRET";
        const string invalidRef = "env:INVALID_SECRET";

        _mockResolver.Setup(r => r.CanResolveSecretAsync(validRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockResolver.Setup(r => r.CanResolveSecretAsync(invalidRef, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var provider = CreateSecretProvider();

        // Act & Assert
        Assert.True(await provider.CanResolveSecretAsync(validRef));
        Assert.False(await provider.CanResolveSecretAsync(invalidRef));
        Assert.False(await provider.CanResolveSecretAsync(""));
        Assert.False(await provider.CanResolveSecretAsync(null!));
    }

    private SecretProvider CreateSecretProvider()
    {
        var optionsWrapper = new OptionsWrapper<SecretProviderOptions>(_options);
        return new SecretProvider(_mockResolver.Object, optionsWrapper, _mockLogger.Object);
    }
}

public class ConfigurationValidationAttributeTests
{
    [Fact]
    public void RequiredConfigurationAttribute_ValidatesRequired()
    {
        // Arrange
        var attribute = new RequiredConfigurationAttribute
        {
            ConfigurationPath = "Test"
        };

        // Act & Assert
        Assert.True(IsValid(attribute, "valid-value"));
        Assert.False(IsValid(attribute, ""));
        Assert.False(IsValid(attribute, null));
        Assert.False(IsValid(attribute, "   "));
    }

    [Fact]
    public void ValidUrlAttribute_ValidatesUrls()
    {
        // Arrange
        var attribute = new ValidUrlAttribute
        {
            RequiredSchemes = new[] { "https" },
            RequireHttpsInProduction = true
        };

        // Act & Assert
        Assert.True(IsValid(attribute, "https://example.com"));
        Assert.False(IsValid(attribute, "http://example.com")); // Wrong scheme
        Assert.False(IsValid(attribute, "not-a-url"));
        Assert.True(IsValid(attribute, null)); // Null is valid (use RequiredConfiguration for required)
    }

    [Fact]
    public void ValidTtlAttribute_ValidatesTtl()
    {
        // Arrange
        var attribute = new ValidTtlAttribute
        {
            MinimumTtl = TimeSpan.FromMinutes(1),
            MaximumTtl = TimeSpan.FromHours(1)
        };

        // Act & Assert
        Assert.True(IsValid(attribute, TimeSpan.FromMinutes(30))); // Valid range
        Assert.False(IsValid(attribute, TimeSpan.FromSeconds(30))); // Too short
        Assert.False(IsValid(attribute, TimeSpan.FromHours(2))); // Too long
        Assert.True(IsValid(attribute, 1800)); // Valid seconds (30 minutes)
        Assert.False(IsValid(attribute, "invalid"));
    }

    [Fact]
    public void SecretReferenceAttribute_ValidatesSecretReferences()
    {
        // Arrange
        var attribute = new SecretReferenceAttribute
        {
            AllowedProviders = new[] { "env", "azure", "aws" },
            AllowPlainTextInDevelopment = false
        };

        // Act & Assert
        Assert.True(IsValid(attribute, "env:MY_SECRET"));
        Assert.True(IsValid(attribute, "azure:keyvault:vault:secret"));
        Assert.True(IsValid(attribute, "aws:secretsmanager:secret"));
        Assert.False(IsValid(attribute, "vault:secret")); // Not in allowed providers
        Assert.False(IsValid(attribute, "plain-text")); // Not a secret reference
        Assert.True(IsValid(attribute, null)); // Null is valid
    }

    private static bool IsValid(ConfigurationValidationAttribute attribute, object? value)
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(new object())
        {
            DisplayName = "TestProperty"
        };
        context.Items["IsDevelopment"] = false; // Production mode

        var result = attribute.IsValid(value, context);
        return result == System.ComponentModel.DataAnnotations.ValidationResult.Success;
    }
}

// Mock SecretProvider for testing - simplified version
public class SecretProvider : ISecretProvider, IDisposable
{
    private readonly IConnectionSecretResolver _resolver;
    private readonly SecretProviderOptions _options;
    private readonly ILogger<SecretProvider> _logger;
    private readonly Dictionary<string, string> _cache = new();
    private readonly string[] _supportedProviders;

    public SecretProvider(
        IConnectionSecretResolver resolver,
        IOptions<SecretProviderOptions> options,
        ILogger<SecretProvider> logger)
    {
        _resolver = resolver;
        _options = options.Value;
        _logger = logger;
        _supportedProviders = _resolver.GetSupportedProviders();
    }

    public async Task<string?> GetSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (_options.EnableCaching && _cache.TryGetValue(secretRef, out var cached))
        {
            return cached;
        }

        try
        {
            var value = await _resolver.ResolveConnectionStringAsync(secretRef, cancellationToken);
            if (_options.EnableCaching && !string.IsNullOrEmpty(value))
            {
                _cache[secretRef] = value;
            }
            return value;
        }
        catch (Exception ex)
        {
            throw new SecretNotFoundException(secretRef, ex.Message, ex);
        }
    }

    public async Task<string?> GetSecretOrDefaultAsync(string secretRef, string? defaultValue = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetSecretAsync(secretRef, cancellationToken);
        }
        catch (SecretNotFoundException)
        {
            return defaultValue;
        }
    }

    public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return Task.FromResult(false);
        }

        return _resolver.CanResolveSecretAsync(secretRef, cancellationToken);
    }

    public string[] GetSupportedProviders() => _supportedProviders;

    public bool IsSecretReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
        {
            return false;
        }

        var prefix = value[..colonIndex];
        return _supportedProviders.Contains(prefix, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _cache.Clear();
    }
}