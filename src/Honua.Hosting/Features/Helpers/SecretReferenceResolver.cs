// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Helpers;

/// <summary>
/// Resolves simple secret references from environment variables.
/// </summary>
internal static class SecretReferenceResolver
{
    private const string EnvPrefix = "env:";

    public static bool IsEnvironmentReference(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveEnvironmentReference(string? value, string settingName)
    {
        if (!IsEnvironmentReference(value))
        {
            return value;
        }

        var envVar = value![EnvPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(envVar))
        {
            throw new InvalidOperationException(
                $"Invalid secret reference for {settingName}. Expected env:VARIABLE_NAME.");
        }

        var resolved = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                $"Environment variable '{envVar}' is not set for {settingName}.");
        }

        return resolved;
    }
}
