// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Honua.Architecture.Tests;

/// <summary>
/// Marks a test as an architecture test (validates code structure and dependencies).
/// Architecture tests enforce design rules and prevent regressions.
/// </summary>
[TraitDiscoverer("Honua.Architecture.Tests.ArchitectureTestDiscoverer", "Honua.Architecture.Tests")]
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
