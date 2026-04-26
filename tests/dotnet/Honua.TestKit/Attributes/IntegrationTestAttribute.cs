// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as an integration test (uses real database, HTTP, etc).
/// Integration tests should comprise ~70% of test suite.
/// Tier=Integration — runs on merge-to-trunk and on PRs scoped via
/// <c>scripts/honua-server-targeted-tests.sh</c>. See ADR-0037.
/// </summary>
[TraitDiscoverer("Honua.TestKit.Attributes.IntegrationTestDiscoverer", "Honua.TestKit")]
public sealed class IntegrationTestAttribute : FactAttribute, ITraitAttribute
{
}

public sealed class IntegrationTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Tier", "Integration")
        ];
    }
}
