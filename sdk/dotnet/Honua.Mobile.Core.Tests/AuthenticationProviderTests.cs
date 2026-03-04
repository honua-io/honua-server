// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Mobile.Core.Auth;

namespace Honua.Mobile.Core.Tests;

/// <summary>
/// Tests for authentication providers.
/// </summary>
public class AuthenticationProviderTests
{
    [Fact]
    public void ApiKeyAuthenticationProvider_WithApiKey_ShouldSetApiKey()
    {
        // Arrange
        const string apiKey = "test-api-key-123";

        // Act
        var provider = new ApiKeyAuthenticationProvider(apiKey);

        // Assert
        provider.Should().NotBeNull();
    }

    [Fact]
    public async Task ApiKeyAuthenticationProvider_GetAuthHeadersAsync_ShouldReturnApiKeyHeader()
    {
        // Arrange
        const string apiKey = "test-api-key-123";
        var provider = new ApiKeyAuthenticationProvider(apiKey);

        // Act
        var headers = await provider.GetAuthHeadersAsync();

        // Assert
        headers.Should().NotBeNull();
        headers.Count.Should().Be(1);

        var header = headers.First();
        header.Key.Should().Be("x-api-key");
        header.Value.Should().Be(apiKey);
    }

    [Fact]
    public async Task ApiKeyAuthenticationProvider_WithoutApiKey_ShouldReturnEmptyHeaders()
    {
        // Arrange
        var provider = new ApiKeyAuthenticationProvider();

        // Act
        var headers = await provider.GetAuthHeadersAsync();

        // Assert
        headers.Should().NotBeNull();
        headers.Count.Should().Be(0);
    }

    [Fact]
    public async Task ApiKeyAuthenticationProvider_SetApiKey_ShouldUpdateApiKey()
    {
        // Arrange
        const string originalKey = "original-key";
        const string newKey = "new-api-key";
        var provider = new ApiKeyAuthenticationProvider(originalKey);

        // Act
        await provider.SetApiKeyAsync(newKey);
        var headers = await provider.GetAuthHeadersAsync();

        // Assert
        var header = headers.First();
        header.Value.Should().Be(newKey);
    }

    [Fact]
    public async Task ApiKeyAuthenticationProvider_ClearCredentials_ShouldRemoveApiKey()
    {
        // Arrange
        const string apiKey = "test-api-key";
        var provider = new ApiKeyAuthenticationProvider(apiKey);

        // Act
        await provider.ClearCredentialsAsync();
        var headers = await provider.GetAuthHeadersAsync();

        // Assert
        headers.Count.Should().Be(0);
    }

    [Fact]
    public async Task ApiKeyAuthenticationProvider_HasCredentials_ShouldReturnCorrectStatus()
    {
        // Arrange
        var provider = new ApiKeyAuthenticationProvider();

        // Act & Assert - Initially no credentials
        var hasCredentials1 = await provider.HasCredentialsAsync();
        hasCredentials1.Should().BeFalse();

        // Set credentials
        await provider.SetApiKeyAsync("test-key");
        var hasCredentials2 = await provider.HasCredentialsAsync();
        hasCredentials2.Should().BeTrue();

        // Clear credentials
        await provider.ClearCredentialsAsync();
        var hasCredentials3 = await provider.HasCredentialsAsync();
        hasCredentials3.Should().BeFalse();
    }

    [Fact]
    public async Task SetApiKeyAsync_WithNullOrEmpty_ShouldThrowArgumentException()
    {
        // Arrange
        var provider = new ApiKeyAuthenticationProvider();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.SetApiKeyAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetApiKeyAsync(string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.SetApiKeyAsync("   "));
    }
}

/// <summary>
/// Tests for secure authentication providers.
/// </summary>
public class SecureAuthenticationProviderTests
{
    [Fact]
    public async Task SecureApiKeyAuthenticationProvider_WithInMemoryStorage_ShouldWorkCorrectly()
    {
        // Arrange
        var storage = new InMemorySecureStorage();
        var provider = new SecureApiKeyAuthenticationProvider(storage);
        const string apiKey = "secure-test-key";

        // Act & Assert - Initially no credentials
        var hasCredentials1 = await provider.HasCredentialsAsync();
        hasCredentials1.Should().BeFalse();

        // Set API key
        await provider.SetApiKeyAsync(apiKey);
        var hasCredentials2 = await provider.HasCredentialsAsync();
        hasCredentials2.Should().BeTrue();

        // Get headers
        var headers = await provider.GetAuthHeadersAsync();
        headers.Count.Should().Be(1);

        var header = headers.First();
        header.Key.Should().Be("x-api-key");
        header.Value.Should().Be(apiKey);

        // Get API key directly
        var retrievedKey = await provider.GetApiKeyAsync();
        retrievedKey.Should().Be(apiKey);

        // Clear credentials
        await provider.ClearCredentialsAsync();
        var hasCredentials3 = await provider.HasCredentialsAsync();
        hasCredentials3.Should().BeFalse();
    }

    [Fact]
    public void SecureApiKeyAuthenticationProvider_WithNullStorage_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new SecureApiKeyAuthenticationProvider(null!));
    }
}

/// <summary>
/// Tests for in-memory secure storage.
/// </summary>
public class InMemorySecureStorageTests
{
    [Fact]
    public async Task SetAsync_AndGetAsync_ShouldStoreAndRetrieveValue()
    {
        // Arrange
        var storage = new InMemorySecureStorage();
        const string key = "test-key";
        const string value = "test-value";

        // Act
        await storage.SetAsync(key, value);
        var retrieved = await storage.GetAsync(key);

        // Assert
        retrieved.Should().Be(value);
        storage.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_WithNonExistentKey_ShouldReturnNull()
    {
        // Arrange
        var storage = new InMemorySecureStorage();

        // Act
        var value = await storage.GetAsync("non-existent-key");

        // Assert
        value.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveValue()
    {
        // Arrange
        var storage = new InMemorySecureStorage();
        const string key = "test-key";
        const string value = "test-value";

        await storage.SetAsync(key, value);

        // Act
        var removed = await storage.RemoveAsync(key);
        var retrievedAfterRemoval = await storage.GetAsync(key);

        // Assert
        removed.Should().BeTrue();
        retrievedAfterRemoval.Should().BeNull();
        storage.Count.Should().Be(0);
    }

    [Fact]
    public async Task RemoveAsync_WithNonExistentKey_ShouldReturnFalse()
    {
        // Arrange
        var storage = new InMemorySecureStorage();

        // Act
        var removed = await storage.RemoveAsync("non-existent-key");

        // Assert
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnCorrectStatus()
    {
        // Arrange
        var storage = new InMemorySecureStorage();
        const string key = "test-key";

        // Act & Assert
        var exists1 = await storage.ContainsKeyAsync(key);
        exists1.Should().BeFalse();

        await storage.SetAsync(key, "value");
        var exists2 = await storage.ContainsKeyAsync(key);
        exists2.Should().BeTrue();

        await storage.RemoveAsync(key);
        var exists3 = await storage.ContainsKeyAsync(key);
        exists3.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAllAsync_ShouldClearAllValues()
    {
        // Arrange
        var storage = new InMemorySecureStorage();
        await storage.SetAsync("key1", "value1");
        await storage.SetAsync("key2", "value2");
        await storage.SetAsync("key3", "value3");

        // Act
        await storage.RemoveAllAsync();

        // Assert
        storage.Count.Should().Be(0);
        (await storage.GetAsync("key1")).Should().BeNull();
        (await storage.GetAsync("key2")).Should().BeNull();
        (await storage.GetAsync("key3")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WithNullOrEmptyKey_ShouldThrowArgumentException()
    {
        // Arrange
        var storage = new InMemorySecureStorage();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.SetAsync(null!, "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SetAsync(string.Empty, "value"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SetAsync("   ", "value"));
    }

    [Fact]
    public async Task SetAsync_WithNullValue_ShouldThrowArgumentNullException()
    {
        // Arrange
        var storage = new InMemorySecureStorage();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => storage.SetAsync("key", null!));
    }
}

/// <summary>
/// Tests for authentication provider factory.
/// </summary>
public class AuthenticationProviderFactoryTests
{
    [Fact]
    public void CreateBasic_ShouldReturnApiKeyAuthenticationProvider()
    {
        // Act
        var provider = AuthenticationProviderFactory.CreateBasic();

        // Assert
        provider.Should().BeOfType<ApiKeyAuthenticationProvider>();
    }

    [Fact]
    public void CreateBasic_WithApiKey_ShouldReturnConfiguredProvider()
    {
        // Arrange
        const string apiKey = "test-api-key";

        // Act
        var provider = AuthenticationProviderFactory.CreateBasic(apiKey);

        // Assert
        provider.Should().BeOfType<ApiKeyAuthenticationProvider>();
    }

    [Fact]
    public void CreateSecure_ShouldReturnSecureAuthenticationProvider()
    {
        // Arrange
        var storage = new InMemorySecureStorage();

        // Act
        var provider = AuthenticationProviderFactory.CreateSecure(storage);

        // Assert
        provider.Should().BeOfType<SecureApiKeyAuthenticationProvider>();
    }

    [Fact]
    public void CreateInMemorySecure_ShouldReturnSecureProviderWithInMemoryStorage()
    {
        // Act
        var provider = AuthenticationProviderFactory.CreateInMemorySecure();

        // Assert
        provider.Should().BeOfType<SecureApiKeyAuthenticationProvider>();
    }

    [Fact]
    public void CreateForPlatform_ShouldReturnPlatformAppropriateProvider()
    {
        // Act
        var provider = AuthenticationProviderFactory.CreateForPlatform();

        // Assert
        provider.Should().BeOfType<SecureApiKeyAuthenticationProvider>();
    }
}