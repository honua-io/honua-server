// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

public sealed class RequiredEnvironmentFactAttribute : FactAttribute
{
    public RequiredEnvironmentFactAttribute(
        string environmentVariable,
        string? requiredValue = null,
        string? skipReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);

        var actualValue = Environment.GetEnvironmentVariable(environmentVariable);
        var shouldRun = requiredValue is null
            ? !string.IsNullOrWhiteSpace(actualValue)
            : string.Equals(actualValue, requiredValue, StringComparison.Ordinal);

        if (!shouldRun)
        {
            Skip = skipReason ?? CreateSkipReason(environmentVariable, requiredValue);
        }
    }

    private static string CreateSkipReason(string environmentVariable, string? requiredValue)
        => requiredValue is null
            ? $"Set {environmentVariable} to run this infrastructure-gated test."
            : $"Set {environmentVariable}={requiredValue} to run this infrastructure-gated test.";
}
