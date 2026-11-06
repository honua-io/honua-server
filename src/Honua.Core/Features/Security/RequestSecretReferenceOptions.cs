// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Security;

// Request-supplied secret references are deny-by-default: with no policy entry, none are resolved.
public sealed partial class RequestSecretReferenceOptions
{
    public const string SectionName = "Security:RequestSecretReferences";

    public IReadOnlyList<string> AllowedEnvironmentVariables { get; init; } = [];

    // An environment prefix never matches a name containing a double underscore, so configuration-binding variables need an exact allow entry.
    public IReadOnlyList<string> AllowedEnvironmentVariablePrefixes { get; init; } = [];

    public IReadOnlyList<string> AllowedSecretReferencePrefixes { get; init; } = [];

    public bool HasEntries =>
        AllowedEnvironmentVariables.Count > 0 ||
        AllowedEnvironmentVariablePrefixes.Count > 0 ||
        AllowedSecretReferencePrefixes.Count > 0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        errors.AddRange(AllowedEnvironmentVariables
            .Where(static name => !EnvironmentVariableNamePattern().IsMatch(name))
            .Select(static name =>
                $"{SectionName}:AllowedEnvironmentVariables entry '{name}' is not a valid environment variable name."));

        errors.AddRange(AllowedEnvironmentVariablePrefixes
            .Where(static prefix => !EnvironmentVariableNamePattern().IsMatch(prefix))
            .Select(static prefix =>
                $"{SectionName}:AllowedEnvironmentVariablePrefixes entry '{prefix}' is not a valid environment variable name prefix."));

        errors.AddRange(AllowedSecretReferencePrefixes
            .Where(static prefix => !RequestSecretReference.TryParse(prefix, out var parsed) || parsed.IsEnvironment)
            .Select(static prefix =>
                $"{SectionName}:AllowedSecretReferencePrefixes entry '{prefix}' must be '<provider>:<identifier-prefix>' for a non-environment provider."));

        return errors;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex EnvironmentVariableNamePattern();
}
