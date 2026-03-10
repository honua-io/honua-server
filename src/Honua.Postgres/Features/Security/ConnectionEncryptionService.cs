// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Security;

/// <summary>
/// Provides AES-GCM encryption for database connection strings with envelope encryption and key rotation support.
/// </summary>
/// <remarks>
/// Implementation details:
/// - Uses AES-256-GCM for authenticated encryption
/// - Implements envelope encryption pattern with data encryption keys (DEKs)
/// - Supports key versioning for rotation
/// - Master key derived from configuration with PBKDF2
/// - Nonce/IV is randomly generated for each encryption operation
/// - Includes integrity verification to prevent tampering
/// </remarks>
internal sealed class ConnectionEncryptionService : IConnectionEncryptionService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConnectionEncryptionService> _logger;
    private readonly byte[] _masterKey;
    private readonly int _currentKeyVersion = 1;
    private bool _disposed;

    // Logger message delegates for performance
    private static readonly Action<ILogger, int, Exception?> _logServiceInitialized =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(_logServiceInitialized)),
            "Connection encryption service initialized with key version {KeyVersion}");

    private static readonly Action<ILogger, int, Exception?> _logEncryptionSuccess =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(2, nameof(_logEncryptionSuccess)),
            "Connection string encrypted successfully with key version {KeyVersion}");

    private static readonly Action<ILogger, Exception?> _logEncryptionFailure =
        LoggerMessage.Define(LogLevel.Error, new EventId(3, nameof(_logEncryptionFailure)),
            "Failed to encrypt connection string");

    private static readonly Action<ILogger, int, Exception?> _logDecryptionSuccess =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(4, nameof(_logDecryptionSuccess)),
            "Connection string decrypted successfully using key version {KeyVersion}");

    private static readonly Action<ILogger, int, Exception?> _logDecryptionFailure =
        LoggerMessage.Define<int>(LogLevel.Error, new EventId(5, nameof(_logDecryptionFailure)),
            "Failed to decrypt connection string with key version {KeyVersion}");

    private static readonly Action<ILogger, Exception?> _logUnexpectedDecryptionError =
        LoggerMessage.Define(LogLevel.Error, new EventId(6, nameof(_logUnexpectedDecryptionError)),
            "Unexpected error during connection string decryption");

    private static readonly Action<ILogger, Exception?> _logKeyRotationNotSupported =
        LoggerMessage.Define(LogLevel.Warning, new EventId(7, nameof(_logKeyRotationNotSupported)),
            "Key rotation attempted but is not supported at runtime. Re-deploy with a new master key passphrase to rotate keys.");

    private static readonly Action<ILogger, Exception?> _logValidationSuccess =
        LoggerMessage.Define(LogLevel.Debug, new EventId(8, nameof(_logValidationSuccess)),
            "Encryption validation successful");

    private static readonly Action<ILogger, Exception?> _logValidationFailure =
        LoggerMessage.Define(LogLevel.Error, new EventId(9, nameof(_logValidationFailure)),
            "Encryption validation failed - decrypted text does not match original");

    private static readonly Action<ILogger, Exception?> _logValidationException =
        LoggerMessage.Define(LogLevel.Error, new EventId(10, nameof(_logValidationException)),
            "Encryption validation failed with exception");

    private static readonly Action<ILogger, Exception?> _logSaltDerived =
        LoggerMessage.Define(LogLevel.Warning, new EventId(11, nameof(_logSaltDerived)),
            "No salt configured. A deterministic salt was derived from the master key. Configure 'Security:ConnectionEncryption:Salt' to control salt rotation explicitly.");

    private static readonly Action<ILogger, string, Exception?> _logSaltFingerprint =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(12, nameof(_logSaltFingerprint)),
            "Salt fingerprint: {Fingerprint}");

    // AES-GCM constants
    private const int AesKeySize = 32; // 256 bits
    private const int NonceSize = 12; // 96 bits (recommended for AES-GCM)
    private const int TagSize = 16; // 128 bits (authentication tag)
    private const int SaltSize = 32; // 256 bits for PBKDF2
    private const int Pbkdf2Iterations = 100000; // NIST recommended minimum

    public ConnectionEncryptionService(
        IConfiguration configuration,
        ILogger<ConnectionEncryptionService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Derive master key from configuration
        _masterKey = DeriveMasterKey();

        // Log initialization (without sensitive data)
        _logServiceInitialized(_logger, _currentKeyVersion, null);
    }

    public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));

        try
        {
            var plaintext = Encoding.UTF8.GetBytes(connectionString);
            var encryptedData = EncryptWithEnvelope(plaintext, _currentKeyVersion);

            _logEncryptionSuccess(_logger, _currentKeyVersion, null);
            return Task.FromResult(encryptedData);
        }
        catch (Exception ex)
        {
            _logEncryptionFailure(_logger, ex);
            throw new InvalidOperationException("Connection string encryption failed", ex);
        }
    }

    public Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(encryptedData);

        if (encryptedData.Length == 0)
            throw new ArgumentException("Encrypted data cannot be empty", nameof(encryptedData));

        if (keyVersion <= 0)
            throw new ArgumentException("Key version must be positive", nameof(keyVersion));

        try
        {
            var decryptedData = DecryptWithEnvelope(encryptedData, keyVersion);
            var connectionString = Encoding.UTF8.GetString(decryptedData);

            _logDecryptionSuccess(_logger, keyVersion, null);
            return Task.FromResult(connectionString);
        }
        catch (CryptographicException ex)
        {
            _logDecryptionFailure(_logger, keyVersion, ex);
            throw new System.Security.Cryptography.CryptographicException("Connection string decryption failed - invalid data or key", ex);
        }
        catch (Exception ex)
        {
            _logUnexpectedDecryptionError(_logger, ex);
            throw new InvalidOperationException("Connection string decryption failed", ex);
        }
    }

    public Task<int> GetCurrentKeyVersionAsync()
    {
        ThrowIfDisposed();
        return Task.FromResult(_currentKeyVersion);
    }

    public Task<int> RotateKeyAsync()
    {
        ThrowIfDisposed();

        _logKeyRotationNotSupported(_logger, null);

        throw new NotSupportedException(
            "In-place key rotation is not supported. The master key is derived from a configured " +
            "passphrase via PBKDF2 and cannot be changed at runtime. To rotate keys, re-deploy " +
            "the application with a new 'Security:ConnectionEncryption:MasterKey' value and " +
            "re-encrypt all stored connection strings.");
    }

    public async Task<bool> ValidateEncryptionAsync()
    {
        ThrowIfDisposed();

        try
        {
            const string testString = "test-connection-string-validation";
            var encrypted = await EncryptConnectionStringAsync(testString);
            var decrypted = await DecryptConnectionStringAsync(encrypted, _currentKeyVersion);

            var isValid = string.Equals(testString, decrypted, StringComparison.Ordinal);

            if (isValid)
            {
                _logValidationSuccess(_logger, null);
            }
            else
            {
                _logValidationFailure(_logger, null);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logValidationException(_logger, ex);
            return false;
        }
    }

    /// <summary>
    /// Encrypts data using envelope encryption pattern.
    /// </summary>
    /// <param name="plaintext">Data to encrypt</param>
    /// <param name="keyVersion">Key version to use</param>
    /// <returns>Encrypted data with envelope structure</returns>
    private byte[] EncryptWithEnvelope(byte[] plaintext, int keyVersion)
    {
        // Generate a random data encryption key (DEK)
        var dek = new byte[AesKeySize];
        RandomNumberGenerator.Fill(dek);

        try
        {
            // Encrypt the plaintext with the DEK
            var encryptedData = EncryptWithAesGcm(plaintext, dek);

            // Encrypt the DEK with the master key (envelope encryption)
            var encryptedDek = EncryptWithAesGcm(dek, _masterKey);

            // Create envelope structure: [version(4)] [encrypted_dek_length(4)] [encrypted_dek] [encrypted_data]
            var envelope = new byte[4 + 4 + encryptedDek.Length + encryptedData.Length];
            var offset = 0;

            // Write key version
            BitConverter.GetBytes(keyVersion).CopyTo(envelope, offset);
            offset += 4;

            // Write encrypted DEK length
            BitConverter.GetBytes(encryptedDek.Length).CopyTo(envelope, offset);
            offset += 4;

            // Write encrypted DEK
            encryptedDek.CopyTo(envelope, offset);
            offset += encryptedDek.Length;

            // Write encrypted data
            encryptedData.CopyTo(envelope, offset);

            return envelope;
        }
        finally
        {
            // Clear DEK from memory
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Decrypts data using envelope encryption pattern.
    /// </summary>
    /// <param name="envelopeData">Encrypted envelope data</param>
    /// <param name="keyVersion">Expected key version</param>
    /// <returns>Decrypted plaintext data</returns>
    private byte[] DecryptWithEnvelope(byte[] envelopeData, int keyVersion)
    {
        if (envelopeData.Length < 8)
            throw new CryptographicException("Invalid envelope data - too short");

        var offset = 0;

        // Read key version
        var dataKeyVersion = BitConverter.ToInt32(envelopeData, offset);
        offset += 4;

        if (dataKeyVersion != keyVersion)
            throw new CryptographicException($"Key version mismatch: expected {keyVersion}, found {dataKeyVersion}");

        // Read encrypted DEK length
        var encryptedDekLength = BitConverter.ToInt32(envelopeData, offset);
        offset += 4;

        if (encryptedDekLength <= 0 || encryptedDekLength > envelopeData.Length - 8)
            throw new CryptographicException("Invalid encrypted DEK length");

        // Extract encrypted DEK
        var encryptedDek = new byte[encryptedDekLength];
        Array.Copy(envelopeData, offset, encryptedDek, 0, encryptedDekLength);
        offset += encryptedDekLength;

        // Extract encrypted data
        var encryptedDataLength = envelopeData.Length - offset;
        if (encryptedDataLength <= 0)
            throw new CryptographicException("No encrypted data found in envelope");

        var encryptedData = new byte[encryptedDataLength];
        Array.Copy(envelopeData, offset, encryptedData, 0, encryptedDataLength);

        // Decrypt DEK with master key
        var dek = DecryptWithAesGcm(encryptedDek, _masterKey);

        try
        {
            // Decrypt data with DEK
            return DecryptWithAesGcm(encryptedData, dek);
        }
        finally
        {
            // Clear DEK from memory
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Encrypts data using AES-GCM.
    /// </summary>
    /// <param name="plaintext">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Encrypted data with nonce and tag</returns>
    private static byte[] EncryptWithAesGcm(byte[] plaintext, byte[] key)
    {
        using var aes = new AesGcm(key, TagSize);

        var nonce = new byte[NonceSize];
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        RandomNumberGenerator.Fill(nonce);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Return: [nonce] [tag] [ciphertext]
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ciphertext.CopyTo(result, NonceSize + TagSize);

        return result;
    }

    /// <summary>
    /// Decrypts data using AES-GCM.
    /// </summary>
    /// <param name="encryptedData">Data to decrypt (nonce + tag + ciphertext)</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted plaintext</returns>
    private static byte[] DecryptWithAesGcm(byte[] encryptedData, byte[] key)
    {
        if (encryptedData.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted data is too short for AES-GCM");

        using var aes = new AesGcm(key, TagSize);

        var nonce = encryptedData.AsSpan(0, NonceSize);
        var tag = encryptedData.AsSpan(NonceSize, TagSize);
        var ciphertext = encryptedData.AsSpan(NonceSize + TagSize);

        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Derives the master encryption key from configuration using PBKDF2.
    /// </summary>
    /// <returns>Master key bytes</returns>
    private byte[] DeriveMasterKey()
    {
        // Get master key material from configuration
        var keyMaterial = _configuration["Security:ConnectionEncryption:MasterKey"];
        if (string.IsNullOrWhiteSpace(keyMaterial))
        {
            throw new InvalidOperationException(
                "Master key not configured. Set 'Security:ConnectionEncryption:MasterKey' to a secure random string.");
        }

        if (keyMaterial.Length < 32)
        {
            throw new InvalidOperationException(
                "Master key must be at least 32 characters long for security.");
        }

        // Get or generate salt
        var saltConfig = _configuration["Security:ConnectionEncryption:Salt"];
        byte[] salt;

        if (!string.IsNullOrWhiteSpace(saltConfig))
        {
            try
            {
                salt = Convert.FromBase64String(saltConfig);
            }
            catch
            {
                throw new InvalidOperationException(
                    "Invalid salt format. Salt must be a valid base64 string.");
            }
        }
        else
        {
            salt = DeriveDeterministicSalt(keyMaterial);
            _logSaltDerived(_logger, null);

            // Log a truncated fingerprint (first 8 chars of SHA256 hash) for debugging without exposing the salt
            var saltHash = SHA256.HashData(salt);
            var fingerprint = Convert.ToHexString(saltHash)[..8];
            _logSaltFingerprint(_logger, fingerprint, null);
        }

        // Derive key using PBKDF2
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(keyMaterial),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            AesKeySize);
    }

    private static byte[] DeriveDeterministicSalt(string keyMaterial)
    {
        var material = Encoding.UTF8.GetBytes($"honua-connection-encryption-salt:v1:{keyMaterial}");
        return SHA256.HashData(material);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // Clear master key from memory
            CryptographicOperations.ZeroMemory(_masterKey);
            _disposed = true;
        }
    }
}
