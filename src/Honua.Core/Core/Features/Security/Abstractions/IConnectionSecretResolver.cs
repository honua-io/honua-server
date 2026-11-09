// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

public interface IConnectionSecretResolver
{
    string ProviderName { get; }

    Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default);

    bool CanResolve(string secretKey);

    Task<bool> CanResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
        => Task.FromResult(CanResolve(secretKey));

    Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default);

    string[] GetSupportedProviders() => [ProviderName];
}
