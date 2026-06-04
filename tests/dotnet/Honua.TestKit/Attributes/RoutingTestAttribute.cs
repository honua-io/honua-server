// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a pgRouting integration test that exercises the real
/// <c>PgRoutingProvider</c> SQL against a live pgRouting database.
/// Skips execution when the required opt-in environment variables are not
/// present so the heavy <c>pgrouting/pgrouting</c> image is never pulled on the
/// default Fast tier. Tier=Slow with Category=Routing — runs only when a
/// developer/CI explicitly opts in (e.g. <c>HONUA_ROUTING_TEST=1</c>).
/// See issue #1266 and ADR-0050.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.RoutingTestDiscoverer", "Honua.TestKit")]
public sealed class RoutingTestAttribute : FactAttribute, ITraitAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RoutingTestAttribute"/> class.
    /// </summary>
    /// <param name="requiredEnvironmentVariables">
    /// Environment variable names that must be set (non-empty) for the test to
    /// run. When any is missing the test is skipped.
    /// </param>
    public RoutingTestAttribute(params string[] requiredEnvironmentVariables)
    {
        if (requiredEnvironmentVariables.Length == 0)
        {
            return;
        }

        var missing = requiredEnvironmentVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();

        if (missing.Length > 0)
        {
            Skip = $"Missing required environment variables: {string.Join(", ", missing)}";
        }
    }
}

/// <summary>
/// Emits the trait metadata for <see cref="RoutingTestAttribute"/>:
/// Category=Integration, Category=Routing, Tier=Slow.
/// </summary>
public sealed class RoutingTestDiscoverer : ITraitDiscoverer
{
    /// <inheritdoc />
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "Routing"),
            new KeyValuePair<string, string>("Tier", Tiers.Slow)
        ];
    }
}
