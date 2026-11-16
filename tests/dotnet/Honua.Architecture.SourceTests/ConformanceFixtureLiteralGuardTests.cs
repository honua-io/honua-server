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
    // Fixture layer names ("cite:Lakes") and the fixture application schema namespaces.
    private static readonly string[] FixtureLiterals = ["\"cite:", "opengis.net/cite/"];

    [ArchitectureTest]
    public void ProductCode_DoesNotNameConformanceFixtureLayers()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repoRoot, "src");

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => ContainsFixtureLiteral(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .ToArray();

        offenders.Should().BeEmpty(
            "layer behaviour is declared in resource metadata (extensions.wms, extensions.wmts, extensions.wfs), never keyed on a fixture layer or schema");
    }

    private static bool ContainsFixtureLiteral(string source)
        => Array.Exists(FixtureLiterals, literal => source.Contains(literal, StringComparison.OrdinalIgnoreCase));
}
