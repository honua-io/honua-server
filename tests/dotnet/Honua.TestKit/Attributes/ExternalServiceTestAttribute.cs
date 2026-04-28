// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as an external service integration test.
/// Skips execution when required environment variables are not present.
/// Tier=Slow with Category=External — runs only on a future external-service-dedicated
/// workflow (real upstream credentials, e.g. Esri Geoportal); the existing nightly
/// slow-tier workflow scopes to Category=Emulator. See ADR-0035.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.ExternalServiceTestDiscoverer", "Honua.TestKit")]
public sealed class ExternalServiceTestAttribute : FactAttribute, ITraitAttribute
{
    public ExternalServiceTestAttribute(params string[] requiredEnvironmentVariables)
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
/// Trait discoverer for external service integration tests.
/// </summary>
public sealed class ExternalServiceTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "External"),
            new KeyValuePair<string, string>("Tier", Tiers.Slow)
        ];
    }
}
