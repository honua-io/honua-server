// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a unit test (isolated, no external dependencies).
/// Unit tests run fastest and should comprise ~20% of test suite.
/// Tier=Fast — included in the PR gate. See ADR-0037.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.UnitTestDiscoverer", "Honua.TestKit")]
public sealed class UnitTestAttribute : FactAttribute, ITraitAttribute
{
}

public sealed class UnitTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Unit"),
            new KeyValuePair<string, string>("Tier", Tiers.Fast)
        ];
    }
}
