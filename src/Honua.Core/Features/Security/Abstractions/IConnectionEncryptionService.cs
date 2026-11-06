// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

public interface IConnectionEncryptionService
{
    Task<byte[]> EncryptConnectionStringAsync(string connectionString);

    Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion);

    Task<int> GetCurrentKeyVersionAsync();

    Task<bool> ValidateEncryptionAsync();

    Task<int> RotateKeyAsync();
}
