// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// A <see cref="FactAttribute"/> that marks a test as <em>skipped</em> when a required
/// environment variable is absent or empty. Use this instead of a silent early
/// <c>return;</c> in the test body so the xUnit runner reports the test as
/// <c>Skipped</c> rather than <c>Passed</c>.
/// </summary>
/// <remarks>
/// <para>Typical usage in a provider integration test that needs an external database:</para>
/// <code>
/// internal sealed class SqlIntegrationFactAttribute()
///     : EnvGatedFactAttribute("HONUA_SQLSERVER_TEST_CONNECTION",
///           "Set HONUA_SQLSERVER_TEST_CONNECTION to run SQL Server integration tests.");
/// </code>
/// </remarks>
public abstract class EnvGatedFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of <see cref="EnvGatedFactAttribute"/>.
    /// If the environment variable named by <paramref name="envVar"/> is absent or
    /// whitespace the test is marked as skipped with <paramref name="skipMessage"/>.
    /// </summary>
    /// <param name="envVar">Name of the environment variable that gates execution.</param>
    /// <param name="skipMessage">Human-readable skip reason surfaced in test output.</param>
    protected EnvGatedFactAttribute(string envVar, string skipMessage)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
        {
            Skip = skipMessage;
        }
    }
}
