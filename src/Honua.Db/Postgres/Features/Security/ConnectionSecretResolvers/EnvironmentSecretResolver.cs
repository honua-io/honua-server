// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

/// <summary>
/// Environment variable-based secret resolver for harness/dev hosts and CI-injected secrets.
/// </summary>
/// <remarks>
/// Secret references use either the <c>env:VARIABLE_NAME</c> or the URI-style
/// <c>env://VARIABLE_NAME</c> form; both resolve to the process environment variable
/// <c>VARIABLE_NAME</c>. A missing or empty variable is a resolution failure
/// (<see cref="SecretNotFoundException"/>) whose message names the variable but never carries
/// a value.
///
/// WARNING: This should only be used for development, harness hosts, or secrets a CI runner
/// injects into the process. Environment variables are visible to the process owner and may be
/// captured by process inspection tooling.
/// </remarks>
internal sealed class EnvironmentSecretResolver : IConnectionSecretResolver
{
    internal const string ProviderType = "env";
    private const string ShortPrefix = ProviderType + ":";
    private const string UriPrefix = ProviderType + "://";

    /// <inheritdoc />
    public string ProviderName => ProviderType;

    /// <inheritdoc />
    public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new ArgumentException("Secret key cannot be null or empty", nameof(secretKey));
        }

        if (!TryGetVariableName(secretKey, out var variableName))
        {
            throw new ArgumentException(
                "Invalid secret key format. Expected 'env:VARIABLE_NAME' or 'env://VARIABLE_NAME'.",
                nameof(secretKey));
        }

        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SecretNotFoundException(
                secretKey,
                $"Environment variable '{variableName}' is not set or is empty.");
        }

        return Task.FromResult<string?>(value);
    }

    /// <inheritdoc />
    public bool CanResolve(string secretKey)
    {
        try
        {
            return TryGetVariableName(secretKey, out var variableName)
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));
        }
        // Intentionally generic: CanResolve is a best-effort capability probe
        // (Environment.GetEnvironmentVariable can only realistically throw under a locked-down
        // security policy); treat any failure as "cannot resolve" rather than adding a logger
        // dependency to this lightweight resolver.
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
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
            var envIndex = result.IndexOf(ShortPrefix, startIndex, StringComparison.OrdinalIgnoreCase);
            if (envIndex == -1)
                break;

            // Find the end of the variable reference (semicolon, space, or end of string)
            var endIndex = envIndex + ShortPrefix.Length;
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

    /// <summary>
    /// Extracts the environment variable name from an <c>env:NAME</c> or <c>env://NAME</c>
    /// reference. The provider prefix is case-insensitive; the variable name is returned as
    /// written.
    /// </summary>
    internal static bool TryGetVariableName(
        [NotNullWhen(true)] string? secretKey,
        [NotNullWhen(true)] out string? variableName)
    {
        variableName = null;
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            return false;
        }

        string candidate;
        if (secretKey.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = secretKey[UriPrefix.Length..];
        }
        else if (secretKey.StartsWith(ShortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = secretKey[ShortPrefix.Length..];
        }
        else
        {
            return false;
        }

        if (candidate.Length == 0
            || candidate.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '=' or '/'))
        {
            return false;
        }

        variableName = candidate;
        return true;
    }
}
