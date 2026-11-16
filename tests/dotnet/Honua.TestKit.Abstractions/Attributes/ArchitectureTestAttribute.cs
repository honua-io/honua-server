// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.Architecture.Tests;

[TraitDiscoverer("Honua.Architecture.Tests.ArchitectureTestDiscoverer", "Honua.TestKit.Abstractions")]
public sealed class ArchitectureTestAttribute : FactAttribute, ITraitAttribute
{
}

public sealed class ArchitectureTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "Architecture");
    }
}
