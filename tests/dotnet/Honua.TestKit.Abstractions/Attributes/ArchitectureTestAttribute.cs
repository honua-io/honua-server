// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.Architecture.Tests;

/// <summary>
/// Marks a test as an architecture test (validates code structure and dependencies).
/// Architecture tests enforce design rules and prevent regressions.
/// </summary>
[TraitDiscoverer("Honua.Architecture.Tests.ArchitectureTestDiscoverer", "Honua.TestKit.Abstractions")]
public sealed class ArchitectureTestAttribute : FactAttribute, ITraitAttribute
{
}

/// <summary>
/// Provides trait metadata for architecture tests.
/// </summary>
public sealed class ArchitectureTestDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "Architecture");
    }
}
