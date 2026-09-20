// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Security;

/// <summary>
/// Operator-owned policy for secret references that arrive in a request (import credentials,
/// workflow source steps, secure-connection registration) rather than in server configuration.
/// </summary>
/// <remarks>
/// <para>
/// Bound from the <c>Security:RequestSecretReferences</c> configuration section. The policy is
/// deny-by-default: when no entry is configured, no request-supplied reference is resolved.
/// Secret references that the server reads from its own configuration (connection strings,
/// admin password, licensing, storage credentials) are not governed by this policy.
/// </para>
/// </remarks>
public sealed partial class RequestSecretReferenceOptions
{
    /// <summary>The configuration section that binds to this options type.</summary>
    public const string SectionName = "Security:RequestSecretReferences";

    /// <summary>
    /// Environment variable names a request may name with <c>env:NAME</c>. Matched exactly
    /// (ordinal, case-sensitive).
    /// </summary>
    public IReadOnlyList<string> AllowedEnvironmentVariables { get; init; } = [];

    /// <summary>
    /// Environment variable name prefixes a request may name with <c>env:NAME</c>. A prefix never
    /// matches a name containing a double underscore, so variables that bind server configuration
    /// (<c>Section__Key</c>) can only be permitted by an exact entry in
    /// <see cref="AllowedEnvironmentVariables"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedEnvironmentVariablePrefixes { get; init; } = [];

    /// <summary>
    /// Whole-reference prefixes a request may name for non-environment providers, including the
    /// provider segment (for example <c>aws:secretsmanager:honua/imports/</c> or
    /// <c>azure:keyvault:honua-imports:</c>). The provider segment is matched case-insensitively,
    /// the remainder ordinally.
    /// </summary>
    public IReadOnlyList<string> AllowedSecretReferencePrefixes { get; init; } = [];

    /// <summary>Whether any entry is configured. When <see langword="false"/> every request-supplied reference is refused.</summary>
    public bool HasEntries =>
        AllowedEnvironmentVariables.Count > 0 ||
        AllowedEnvironmentVariablePrefixes.Count > 0 ||
        AllowedSecretReferencePrefixes.Count > 0;

    /// <summary>
    /// Validates the configured entries and returns one message per invalid entry. An empty result
    /// means the options are valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        foreach (var name in AllowedEnvironmentVariables)
        {
            if (!EnvironmentVariableNamePattern().IsMatch(name))
            {
                errors.Add($"{SectionName}:AllowedEnvironmentVariables entry '{name}' is not a valid environment variable name.");
            }
        }

        foreach (var prefix in AllowedEnvironmentVariablePrefixes)
        {
            if (!EnvironmentVariableNamePattern().IsMatch(prefix))
            {
                errors.Add($"{SectionName}:AllowedEnvironmentVariablePrefixes entry '{prefix}' is not a valid environment variable name prefix.");
            }
        }

        foreach (var prefix in AllowedSecretReferencePrefixes)
        {
            if (!RequestSecretReference.TryParse(prefix, out var parsed) ||
                parsed.IsEnvironment)
            {
                errors.Add(
                    $"{SectionName}:AllowedSecretReferencePrefixes entry '{prefix}' must be '<provider>:<identifier-prefix>' for a non-environment provider.");
            }
        }

        return errors;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex EnvironmentVariableNamePattern();
}
