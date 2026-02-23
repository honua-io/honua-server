// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

/// <summary>
/// Comprehensive tests for the connection encryption service.
/// </summary>
/// <remarks>
/// Tests cover:
/// - Encryption/decryption round-trips
/// - Key versioning and rotation
/// - Security properties (authentication, integrity)
/// - Error conditions and edge cases
/// - Performance characteristics
/// </remarks>
[Collection("Security")]
public class ConnectionEncryptionServiceTests : IDisposable
{
    private readonly ConnectionEncryptionService _encryptionService;
    private readonly IConfiguration _configuration;

    public ConnectionEncryptionServiceTests()
    {
        // Create test configuration with secure test key
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>("Security:ConnectionEncryption:MasterKey",
                "test-master-key-that-is-at-least-32-characters-long-for-security"),
            new KeyValuePair<string, string?>("Security:ConnectionEncryption:Salt",
                "dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=") // base64 encoded test salt
        });
        _configuration = configBuilder.Build();

        _encryptionService = new ConnectionEncryptionService(_configuration, NullLogger<ConnectionEncryptionService>.Instance);
    }

    [SecurityTest]
    [Fact]
    public async Task EncryptConnectionStringAsync_ValidString_ReturnsEncryptedData()
    {
        // Arrange
        const string testConnectionString = "Host=localhost;Database=test;Username=user;Password=secret";

        // Act
        var encryptedData = await _encryptionService.EncryptConnectionStringAsync(testConnectionString);

        // Assert
        Assert.NotNull(encryptedData);
        Assert.NotEmpty(encryptedData);
        Assert.True(encryptedData.Length > testConnectionString.Length); // Should be larger due to encryption overhead
    }

    [SecurityTest]
    [Fact]
    public async Task DecryptConnectionStringAsync_ValidEncryptedData_ReturnsOriginalString()
    {
        // Arrange
        const string originalConnectionString = "Host=localhost;Database=test;Username=user;Password=secret";
        var encryptedData = await _encryptionService.EncryptConnectionStringAsync(originalConnectionString);
        var keyVersion = await _encryptionService.GetCurrentKeyVersionAsync();

        // Act
        var decryptedString = await _encryptionService.DecryptConnectionStringAsync(encryptedData, keyVersion);

        // Assert
        Assert.Equal(originalConnectionString, decryptedString);
    }

    [SecurityTest]
    [Fact]
    public async Task EncryptDecrypt_RoundTrip_PreservesDataIntegrity()
    {
        // Test various connection string formats
        var testCases = new[]
        {
            "Host=localhost;Port=5432;Database=test;Username=user;Password=pass123",
            "Host=db.example.com;Database=production;Username=app_user;Password=complex!@#$%^&*()_+password",
            "Host=127.0.0.1;Database=unicode_test;Username=user;Password=测试密码",
            "Host=ssl-host.com;Database=secure;Username=ssl_user;Password=secure_pass;SslMode=Require;TrustServerCertificate=true"
        };

        foreach (var testConnectionString in testCases)
        {
            // Act
            var encryptedData = await _encryptionService.EncryptConnectionStringAsync(testConnectionString);
            var keyVersion = await _encryptionService.GetCurrentKeyVersionAsync();
            var decryptedString = await _encryptionService.DecryptConnectionStringAsync(encryptedData, keyVersion);

            // Assert
            Assert.Equal(testConnectionString, decryptedString);
        }
    }

    [SecurityTest]
    [Fact]
    public async Task EncryptConnectionStringAsync_NullOrEmptyInput_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _encryptionService.EncryptConnectionStringAsync(null!));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _encryptionService.EncryptConnectionStringAsync(""));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _encryptionService.EncryptConnectionStringAsync("   "));
    }

    [SecurityTest]
    [Fact]
    public async Task DecryptConnectionStringAsync_InvalidInput_ThrowsException()
    {
        var keyVersion = await _encryptionService.GetCurrentKeyVersionAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _encryptionService.DecryptConnectionStringAsync(null!, keyVersion));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _encryptionService.DecryptConnectionStringAsync(Array.Empty<byte>(), keyVersion));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _encryptionService.DecryptConnectionStringAsync(new byte[] { 1, 2, 3 }, 0));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _encryptionService.DecryptConnectionStringAsync(new byte[] { 1, 2, 3 }, -1));
    }

    [SecurityTest]
    [Fact]
    public async Task DecryptConnectionStringAsync_CorruptedData_ThrowsCryptographicException()
    {
        // Arrange
        const string testConnectionString = "Host=localhost;Database=test;Username=user;Password=secret";
        var encryptedData = await _encryptionService.EncryptConnectionStringAsync(testConnectionString);
        var keyVersion = await _encryptionService.GetCurrentKeyVersionAsync();

        // Corrupt the encrypted data
        encryptedData[encryptedData.Length / 2] ^= 0xFF;

        // Act & Assert
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() =>
            _encryptionService.DecryptConnectionStringAsync(encryptedData, keyVersion));
    }

    [SecurityTest]
    [Fact]
    public async Task DecryptConnectionStringAsync_WrongKeyVersion_ThrowsCryptographicException()
    {
        // Arrange
        const string testConnectionString = "Host=localhost;Database=test;Username=user;Password=secret";
        var encryptedData = await _encryptionService.EncryptConnectionStringAsync(testConnectionString);

        // Act & Assert - Try to decrypt with wrong key version
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() =>
            _encryptionService.DecryptConnectionStringAsync(encryptedData, 999));
    }

    [SecurityTest]
    [Fact]
    public async Task GetCurrentKeyVersionAsync_ReturnsValidVersion()
    {
        // Act
        var keyVersion = await _encryptionService.GetCurrentKeyVersionAsync();

        // Assert
        Assert.True(keyVersion > 0);
    }

    [SecurityTest]
    [Fact]
    public async Task RotateKeyAsync_ThrowsNotSupportedException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _encryptionService.RotateKeyAsync());
    }

    [SecurityTest]
    [Fact]
    public async Task ValidateEncryptionAsync_WithValidConfiguration_ReturnsTrue()
    {
        // Act
        var isValid = await _encryptionService.ValidateEncryptionAsync();

        // Assert
        Assert.True(isValid);
    }

    [SecurityTest]
    [Fact]
    public async Task EncryptionDeterministic_SameInput_ProducesDifferentOutput()
    {
        // Arrange
        const string testConnectionString = "Host=localhost;Database=test;Username=user;Password=secret";

        // Act - Encrypt the same string multiple times
        var encrypted1 = await _encryptionService.EncryptConnectionStringAsync(testConnectionString);
        var encrypted2 = await _encryptionService.EncryptConnectionStringAsync(testConnectionString);

        // Assert - Results should be different due to random nonce/IV
        Assert.False(encrypted1.SequenceEqual(encrypted2));

        // But both should decrypt to the same plaintext
        var keyVersion = await _encryptionService.GetCurrentKeyVersionAsync();
        var decrypted1 = await _encryptionService.DecryptConnectionStringAsync(encrypted1, keyVersion);
        var decrypted2 = await _encryptionService.DecryptConnectionStringAsync(encrypted2, keyVersion);

        Assert.Equal(testConnectionString, decrypted1);
        Assert.Equal(testConnectionString, decrypted2);
    }

    [SecurityTest]
    [Fact]
    public async Task EncryptionPerformance_LargeConnectionString_CompletesInReasonableTime()
    {
        // Arrange - Create a large connection string (simulate complex configurations)
        var largeConnectionString = string.Join(";", Enumerable.Range(0, 1000)
            .Select(i => $"Param{i}=Value{i}"));

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var encryptedData = await _encryptionService.EncryptConnectionStringAsync(largeConnectionString);
        var encryptTime = stopwatch.Elapsed;

        stopwatch.Restart();
        var keyVersion = await _encryptionService.GetCurrentKeyVersionAsync();
        var decryptedString = await _encryptionService.DecryptConnectionStringAsync(encryptedData, keyVersion);
        var decryptTime = stopwatch.Elapsed;

        // Assert
        Assert.Equal(largeConnectionString, decryptedString);
        Assert.True(encryptTime < TimeSpan.FromSeconds(1), $"Encryption took too long: {encryptTime}");
        Assert.True(decryptTime < TimeSpan.FromSeconds(1), $"Decryption took too long: {decryptTime}");
    }

    [SecurityTest]
    [Fact]
    public async Task Dispose_DisposedService_ThrowsObjectDisposedException()
    {
        // Arrange
        _encryptionService.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _encryptionService.EncryptConnectionStringAsync("test"));

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _encryptionService.DecryptConnectionStringAsync(new byte[] { 1, 2, 3 }, 1));

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _encryptionService.GetCurrentKeyVersionAsync());

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _encryptionService.RotateKeyAsync());

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _encryptionService.ValidateEncryptionAsync());
    }

    public void Dispose()
    {
        _encryptionService?.Dispose();
        GC.SuppressFinalize(this);
    }
}
