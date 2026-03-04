// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Grpc.Core;

namespace Honua.Mobile.Core.Auth;

/// <summary>
/// Provides authentication for mobile gRPC requests.
/// Supports API key authentication with secure platform storage.
/// </summary>
public interface IMobileAuthenticationProvider
{
    /// <summary>
    /// Gets authentication headers for gRPC requests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>gRPC metadata headers including authentication</returns>
    Task<Metadata> GetAuthHeadersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the API key for authentication.
    /// The key will be stored securely using platform-specific storage.
    /// </summary>
    /// <param name="apiKey">API key to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears any stored authentication credentials.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ClearCredentialsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the provider has valid authentication credentials.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if credentials are available</returns>
    Task<bool> HasCredentialsAsync(CancellationToken cancellationToken = default);
}