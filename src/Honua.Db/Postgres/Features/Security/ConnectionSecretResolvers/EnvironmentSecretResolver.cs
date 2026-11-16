// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

internal sealed class EnvironmentSecretResolver : IConnectionSecretResolver
{
    internal const string ProviderType = "env";
    private const string ShortPrefix = ProviderType + ":";
    private const string UriPrefix = ProviderType + "://";

    public string ProviderName => ProviderType;

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

    public Task<string?> ResolveSecretFreshAsync(string secretKey, CancellationToken cancellationToken = default)
        => ResolveSecretAsync(secretKey, cancellationToken);

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
