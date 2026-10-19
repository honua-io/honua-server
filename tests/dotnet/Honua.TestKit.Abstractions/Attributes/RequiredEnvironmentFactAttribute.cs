// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a fact that requires an opt-in environment variable and reports it as skipped
/// when the required infrastructure is not configured.
/// </summary>
public sealed class RequiredEnvironmentFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RequiredEnvironmentFactAttribute"/> class.
    /// </summary>
    /// <param name="environmentVariable">The environment variable that gates the test.</param>
    /// <param name="requiredValue">
    /// Optional exact value required for the test to run. When omitted, any non-empty value is accepted.
    /// </param>
    /// <param name="skipReason">Optional skip message used when the environment requirement is not met.</param>
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
