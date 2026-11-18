// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class InvariantGlobalizationSourceTests
{
    // Matches qualified names and target-typed construction too, e.g.
    // new System.Globalization.RegionInfo(name) or RegionInfo region = new(name).
    private static readonly Regex CultureDataCall = new(
        @"\bGetCultures\s*\(|\bRegionInfo\s*\(|\bRegionInfo\s+\w+\s*=\s*new\s*\(",
        RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void ServerSource_ShouldNotDependOnCultureEnumerationOrRegionData()
    {
        var sourceRoot = ArchitectureTestHelpers.CombinePath(ArchitectureTestHelpers.ResolveRepositoryRoot(), "src");

        var offending = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => CultureDataCall.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .ToList();

        offending.Should().BeEmpty(
            "the postgis AOT image builds with InvariantGlobalization=true, where culture enumeration " +
            "returns only the invariant culture and RegionInfo cannot be constructed; ship the data instead " +
            "(see Honua.Geocoding country-aliases.tsv)");
    }
}
