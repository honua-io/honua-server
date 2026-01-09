// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Provides encryption and decryption services for database connection strings using AES-GCM with envelope encryption.
/// </summary>
/// <remarks>
/// This service implements envelope encryption where:
/// 1. A master key is used to encrypt/decrypt data encryption keys (DEKs)
/// 2. DEKs are used to encrypt/decrypt the actual connection strings
/// 3. Only encrypted DEKs and encrypted data are stored, never plaintext keys
///
/// Key rotation is supported through version management.
/// </remarks>
public interface IConnectionEncryptionService
{
    /// <summary>
    /// Encrypts a connection string using the current encryption key version.
    /// </summary>
    /// <param name="connectionString">The plaintext connection string to encrypt</param>
    /// <returns>Encrypted connection string as byte array</returns>
    /// <exception cref="ArgumentNullException">Thrown when connectionString is null</exception>
    /// <exception cref="ArgumentException">Thrown when connectionString is empty or whitespace</exception>
    /// <exception cref="InvalidOperationException">Thrown when no active encryption key is available</exception>
    Task<byte[]> EncryptConnectionStringAsync(string connectionString);

    /// <summary>
    /// Decrypts a connection string using the specified key version.
    /// </summary>
    /// <param name="encryptedData">The encrypted connection string data</param>
    /// <param name="keyVersion">The encryption key version used for encryption</param>
    /// <returns>Decrypted plaintext connection string</returns>
    /// <exception cref="ArgumentNullException">Thrown when encryptedData is null</exception>
    /// <exception cref="ArgumentException">Thrown when encryptedData is empty</exception>
    /// <exception cref="InvalidOperationException">Thrown when the specified key version is not available</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Thrown when decryption fails (invalid data, wrong key, etc.)</exception>
    Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion);

    /// <summary>
    /// Gets the current active encryption key version.
    /// </summary>
    /// <returns>The current key version number</returns>
    /// <exception cref="InvalidOperationException">Thrown when no active encryption key is available</exception>
    Task<int> GetCurrentKeyVersionAsync();

    /// <summary>
    /// Initiates key rotation by creating a new encryption key version.
    /// </summary>
    /// <returns>The new key version number</returns>
    /// <remarks>
    /// After rotation, new encryptions will use the new key version.
    /// Existing encrypted data remains accessible using old key versions.
    /// Old keys should be retired after all data is re-encrypted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when key rotation fails</exception>
    Task<int> RotateKeyAsync();

    /// <summary>
    /// Validates that encryption is working correctly by performing a round-trip test.
    /// </summary>
    /// <returns>True if encryption/decryption is working correctly</returns>
    /// <remarks>
    /// This method is useful for health checks and ensuring the encryption service
    /// is properly configured and functional.
    /// </remarks>
    Task<bool> ValidateEncryptionAsync();
}
