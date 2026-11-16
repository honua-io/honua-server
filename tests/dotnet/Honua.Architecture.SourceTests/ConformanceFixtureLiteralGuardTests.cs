// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// A conformance suite must observe the same behaviour as any other client, so product code
// may not branch on the names of the suite's fixture layers.
[Trait("Category", "Architecture")]
public sealed class ConformanceFixtureLiteralGuardTests
{
    private const string FixtureLayerLiteralPrefix = "\"cite:";

    [ArchitectureTest]
    public void ProductCode_DoesNotNameConformanceFixtureLayers()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repoRoot, "src");

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(FixtureLayerLiteralPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .ToArray();

        offenders.Should().BeEmpty(
            "layer behaviour is declared in resource metadata (for example extensions.wms.dimensions), never keyed on a fixture layer name");
    }
}
