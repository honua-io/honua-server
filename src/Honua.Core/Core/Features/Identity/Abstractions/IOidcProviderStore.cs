// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Identity.Domain;

namespace Honua.Core.Features.Identity.Abstractions;

public interface IOidcProviderStore
{
    Task<IReadOnlyList<OidcProviderConfiguration>> ListProvidersAsync(CancellationToken cancellationToken = default);

    Task<OidcProviderConfiguration?> GetProviderAsync(Guid providerId, CancellationToken cancellationToken = default);

    Task<OidcProviderConfiguration> CreateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default);

    Task<OidcProviderConfiguration?> UpdateProviderAsync(OidcProviderConfiguration provider, CancellationToken cancellationToken = default);

    Task<bool> DeleteProviderAsync(Guid providerId, CancellationToken cancellationToken = default);

    Task<OidcProviderTestResult> TestProviderAsync(Guid providerId, CancellationToken cancellationToken = default);
}

public sealed class OidcProviderTestResult
{
    public required bool IsReachable { get; init; }

    public required string Message { get; init; }

    public DateTimeOffset TestedAt { get; init; } = DateTimeOffset.UtcNow;
}
