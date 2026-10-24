// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Provider query builders call <c>WhereClauseSplitter.SplitOnAnd</c>. Postgres
/// keeps its own copy because that splitter also handles double quotes and
/// whitespace. A new private copy fails this test instead of growing another
/// provider-local parser.
/// </summary>
[Trait("Category", "Architecture")]
public sealed partial class WhereClauseSplitterAllowlistTests
{
    private static readonly string[] AllowedDeclarations =
    [
        "src/Honua.Core/Queries/Filters/WhereClauseSplitter.cs",
        "src/Honua.Db/Postgres/Features/FeatureStore/Services/FeatureQueryBuilder.Where.cs",
        "src/Honua.Db/Postgres/Features/FeatureStore/Services/PostgresStorageMappedFeatureReader.cs",
    ];

    [ArchitectureTest]
    public void SplitOnAnd_IsDeclaredOnlyByTheSharedHelperAndPostgres()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var src = ArchitectureTestHelpers.CombinePath(root, "src");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (Declaration().IsMatch(text))
            {
                found.Add(relative);
            }
        }

        found.Sort(StringComparer.Ordinal);
        found.Should().Equal(AllowedDeclarations);
    }

    [GeneratedRegex(
        """\bList<string>\s+SplitOnAnd\s*\(""",
        RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();
}
