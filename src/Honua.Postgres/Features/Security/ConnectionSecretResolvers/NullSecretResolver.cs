// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;

namespace Honua.Postgres.Features.Security.ConnectionSecretResolvers;

/// <summary>
/// Null implementation of secret resolver that doesn't support any external secret providers.
/// </summary>
/// <remarks>
/// This implementation is used as a fallback when no external secret management
/// systems are configured. It will always fail to resolve secrets, encouraging
/// the use of encrypted storage instead.
/// </remarks>
internal sealed class NullSecretResolver : IConnectionSecretResolver
{
    public Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"External secret resolution is not configured. Secret reference '{secretRef}' cannot be resolved. " +
            "Either configure a secret management provider or use encrypted credential storage.");
    }

    public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        // Always return false since this resolver doesn't support any providers
        return Task.FromResult(false);
    }

    public string[] GetSupportedProviders()
    {
        // No providers supported
        return Array.Empty<string>();
    }
}

/// <summary>
/// Environment variable-based secret resolver for development and simple deployments.
/// </summary>
/// <remarks>
/// This resolver allows secrets to be stored in environment variables.
/// Secret references use the format: "env:VARIABLE_NAME"
///
/// WARNING: This should only be used for development or simple deployments.
/// Environment variables are visible in process lists and may be logged.
/// </remarks>
internal sealed class EnvironmentSecretResolver : IConnectionSecretResolver
{
    private const string ProviderType = "env";

    public Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new ArgumentException("Secret reference cannot be null or empty", nameof(secretRef));

        if (!secretRef.StartsWith($"{ProviderType}:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid secret reference format. Expected 'env:VARIABLE_NAME', got '{secretRef}'", nameof(secretRef));

        var variableName = secretRef[4..]; // Remove "env:" prefix
        if (string.IsNullOrWhiteSpace(variableName))
            throw new ArgumentException("Environment variable name cannot be empty", nameof(secretRef));

        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{variableName}' is not set or is empty");
        }

        return Task.FromResult(value);
    }

    public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(secretRef) || !secretRef.StartsWith($"{ProviderType}:", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);

            var variableName = secretRef[4..];
            if (string.IsNullOrWhiteSpace(variableName))
                return Task.FromResult(false);

            var value = Environment.GetEnvironmentVariable(variableName);
            return Task.FromResult(!string.IsNullOrWhiteSpace(value));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public string[] GetSupportedProviders()
    {
        return new[] { ProviderType };
    }
}
