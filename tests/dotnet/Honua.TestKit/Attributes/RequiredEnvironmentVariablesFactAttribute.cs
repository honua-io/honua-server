// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a fact that requires several non-empty environment variables.
/// </summary>
public sealed class RequiredEnvironmentVariablesFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RequiredEnvironmentVariablesFactAttribute"/> class.
    /// </summary>
    /// <param name="environmentVariables">Environment variables that must all be non-empty.</param>
    public RequiredEnvironmentVariablesFactAttribute(params string[] environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        if (environmentVariables.Length == 0 || environmentVariables.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one valid environment variable is required.", nameof(environmentVariables));
        }

        var missing = environmentVariables
            .Where(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            .ToArray();

        if (missing.Length > 0)
        {
            Skip = $"missing-credential:{string.Join(',', missing)}";
        }
    }
}
