// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

public sealed class RequiredEnvironmentVariablesFactAttribute : FactAttribute
{
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
