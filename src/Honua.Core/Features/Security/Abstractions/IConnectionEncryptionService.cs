// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Service for handling connection encryption.
/// </summary>
public interface IConnectionEncryptionService
{
    /// <summary>
    /// Encrypts a connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to encrypt</param>
    /// <returns>Encrypted connection string data</returns>
    Task<byte[]> EncryptConnectionStringAsync(string connectionString);

    /// <summary>
    /// Decrypts a connection string.
    /// </summary>
    /// <param name="encryptedData">The encrypted connection string data</param>
    /// <param name="keyVersion">The key version used for encryption</param>
    /// <returns>Decrypted connection string</returns>
    Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion);

    /// <summary>
    /// Gets the current encryption key version.
    /// </summary>
    /// <returns>Current key version</returns>
    Task<int> GetCurrentKeyVersionAsync();

    /// <summary>
    /// Validates that encryption is working correctly.
    /// </summary>
    /// <returns>True if encryption is working correctly</returns>
    Task<bool> ValidateEncryptionAsync();

    /// <summary>
    /// Rotates the encryption key and returns the new version.
    /// </summary>
    /// <returns>The new key version.</returns>
    Task<int> RotateKeyAsync();
}
