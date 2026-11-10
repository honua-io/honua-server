// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

public interface ISecretProvider
{
    Task<string?> GetSecretAsync(string secretKey, CancellationToken cancellationToken = default);

    Task<string?> GetSecretOrDefaultAsync(string secretKey, string? defaultValue = null, CancellationToken cancellationToken = default);

    bool CanProvideSecret(string secretKey);

    Task<bool> CanResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default);

    string ProviderName { get; }

    string[] GetSupportedProviders();

    bool IsSecretReference(string? value);
}
