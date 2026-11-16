// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit.Constants;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.TestKit.Attributes;

[TraitDiscoverer("Honua.TestKit.Attributes.UnitTestDiscoverer", "Honua.TestKit.Abstractions")]
public sealed class UnitTestAttribute : FactAttribute, ITraitAttribute
{
}

[TraitDiscoverer("Honua.TestKit.Attributes.UnitTestDiscoverer", "Honua.TestKit.Abstractions")]
public sealed class UnitTheoryAttribute : TheoryAttribute, ITraitAttribute
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
