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
    /// <inheritdoc />
    public string ProviderName => "null";

    /// <inheritdoc />
    public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"External secret resolution is not configured. Secret reference '{secretKey}' cannot be resolved. " +
            "Either configure a secret management provider or use encrypted credential storage.");
    }

    /// <inheritdoc />
    public bool CanResolve(string secretKey)
    {
        // Always return false since this resolver doesn't support any providers
        return false;
    }

    /// <inheritdoc />
    public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"External secret resolution is not configured. Connection string template '{connectionStringTemplate}' cannot be resolved. " +
            "Either configure a secret management provider or use encrypted credential storage.");
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

    /// <inheritdoc />
    public string ProviderName => ProviderType;

    /// <inheritdoc />
    public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Secret key cannot be null or empty", nameof(secretKey));

        if (!secretKey.StartsWith($"{ProviderType}:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid secret key format. Expected 'env:VARIABLE_NAME', got '{secretKey}'", nameof(secretKey));

        var variableName = secretKey[4..]; // Remove "env:" prefix
        if (string.IsNullOrWhiteSpace(variableName))
            throw new ArgumentException("Environment variable name cannot be empty", nameof(secretKey));

        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{variableName}' is not set or is empty");
        }

        return Task.FromResult<string?>(value);
    }

    /// <inheritdoc />
    public bool CanResolve(string secretKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(secretKey) || !secretKey.StartsWith($"{ProviderType}:", StringComparison.OrdinalIgnoreCase))
                return false;

            var variableName = secretKey[4..];
            if (string.IsNullOrWhiteSpace(variableName))
                return false;

            var value = Environment.GetEnvironmentVariable(variableName);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionStringTemplate))
            return connectionStringTemplate;

        // Simple pattern to find env: references in connection strings
        // This is a basic implementation - could be enhanced with more sophisticated parsing
        var result = connectionStringTemplate;
        var startIndex = 0;

        while (true)
        {
            var envIndex = result.IndexOf($"{ProviderType}:", startIndex, StringComparison.OrdinalIgnoreCase);
            if (envIndex == -1)
                break;

            // Find the end of the variable reference (semicolon, space, or end of string)
            var endIndex = envIndex + 4; // Start after "env:"
            while (endIndex < result.Length &&
                   result[endIndex] != ';' &&
                   result[endIndex] != ' ' &&
                   result[endIndex] != '\t')
            {
                endIndex++;
            }

            var secretKey = result[envIndex..endIndex];
            var resolvedValue = await ResolveSecretAsync(secretKey, cancellationToken);

            if (!string.IsNullOrEmpty(resolvedValue))
            {
                result = result.Replace(secretKey, resolvedValue);
                startIndex = envIndex + resolvedValue.Length;
            }
            else
            {
                startIndex = endIndex;
            }
        }

        return result;
    }
}
