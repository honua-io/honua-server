// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Provides resolution of database connection strings from external secret management systems.
/// </summary>
/// <remarks>
/// This interface supports integration with various secret management services:
/// - Azure Key Vault
/// - AWS Secrets Manager
/// - GCP Secret Manager
/// - HashiCorp Vault
/// - Kubernetes Secrets
/// - Custom secret providers
///
/// Secret references use a URI-like format: {provider}:{path}
/// Examples:
/// - "aws:secretsmanager:prod-db-credentials"
/// - "azure:keyvault:my-vault:database-connection"
/// - "gcp:secretmanager:my-project:database-connection"
/// - "vault:secret/database/prod"
/// </remarks>
public interface IConnectionSecretResolver
{
    /// <summary>
    /// Resolves a connection string from an external secret reference.
    /// </summary>
    /// <param name="secretRef">Secret reference in provider:path format</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved connection string</returns>
    /// <exception cref="ArgumentNullException">Thrown when secretRef is null</exception>
    /// <exception cref="ArgumentException">Thrown when secretRef format is invalid</exception>
    /// <exception cref="InvalidOperationException">Thrown when the secret provider is not supported</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when access to the secret is denied</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">Thrown when secret service is unreachable</exception>
    Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests whether a secret reference can be resolved without retrieving the actual value.
    /// </summary>
    /// <param name="secretRef">Secret reference to test</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the secret reference is valid and accessible</returns>
    /// <remarks>
    /// This method is useful for validating secret references during configuration
    /// without exposing the actual secret values in logs or responses.
    /// </remarks>
    Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the supported secret provider types.
    /// </summary>
    /// <returns>Array of supported provider names</returns>
    string[] GetSupportedProviders();
}

/// <summary>
/// Factory for creating secret resolver instances based on provider type.
/// </summary>
public interface IConnectionSecretResolverFactory
{
    /// <summary>
    /// Creates a secret resolver for the specified provider type.
    /// </summary>
    /// <param name="providerType">The secret provider type (e.g., "aws", "azure", "vault")</param>
    /// <returns>Secret resolver instance</returns>
    /// <exception cref="ArgumentNullException">Thrown when providerType is null</exception>
    /// <exception cref="ArgumentException">Thrown when providerType is not supported</exception>
    IConnectionSecretResolver CreateResolver(string providerType);

    /// <summary>
    /// Gets all supported provider types across all registered resolvers.
    /// </summary>
    /// <returns>Array of all supported provider names</returns>
    string[] GetAllSupportedProviders();
}
