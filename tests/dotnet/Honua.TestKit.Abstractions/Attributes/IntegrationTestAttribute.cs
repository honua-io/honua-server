// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

[TraitDiscoverer("Honua.TestKit.Attributes.IntegrationTestDiscoverer", "Honua.TestKit.Abstractions")]
public sealed class IntegrationTestAttribute : FactAttribute, ITraitAttribute
{
}

[TraitDiscoverer("Honua.TestKit.Attributes.IntegrationTestDiscoverer", "Honua.TestKit.Abstractions")]
public sealed class IntegrationTheoryAttribute : TheoryAttribute, ITraitAttribute
{
}

public sealed class IntegrationTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        return
        [
            new KeyValuePair<string, string>("Category", "Integration"),
            new KeyValuePair<string, string>("Tier", Tiers.Integration)
        ];
    }
}
