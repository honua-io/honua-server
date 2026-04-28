// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a multi-node scale test.
/// Skips execution when required environment variables are not present.
/// Tier=Slow with Category=Scale — runs only on a future scale-dedicated workflow
/// (multi-node compose fixtures); the existing nightly slow-tier workflow scopes
/// to Category=Emulator. See ADR-0035.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.ScaleTestDiscoverer", "Honua.TestKit")]
public sealed class ScaleTestAttribute : FactAttribute, ITraitAttribute
{
    public ScaleTestAttribute(params string[] requiredEnvironmentVariables)
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

public sealed class ScaleTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Category", "Scale"),
            new KeyValuePair<string, string>("Tier", Tiers.Slow)
        ];
    }
}
