// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a fact that requires an exact opt-in value and several non-empty environment variables.
/// </summary>
public sealed class RequiredEnvironmentVariablesFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RequiredEnvironmentVariablesFactAttribute"/> class.
    /// </summary>
    /// <param name="enableVariable">Environment variable that explicitly opts into the test.</param>
    /// <param name="requiredValue">Exact value required to opt into the test.</param>
    /// <param name="environmentVariables">Additional environment variables that must all be non-empty.</param>
    public RequiredEnvironmentVariablesFactAttribute(
        string enableVariable,
        string requiredValue,
        params string[] environmentVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enableVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredValue);
        ArgumentNullException.ThrowIfNull(environmentVariables);
        if (environmentVariables.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Environment variable names must be non-empty.", nameof(environmentVariables));
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(enableVariable), requiredValue, StringComparison.Ordinal))
        {
            Skip = $"opt-in-required:{enableVariable}={requiredValue}";
            return;
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
