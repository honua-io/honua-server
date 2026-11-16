// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Honua.Architecture.Tests;

// These project trees carry no XML doc comments; the clean-code check only looks at added
// lines, so this guard keeps the whole tree at zero. It reads documentation-comment trivia
// from the syntax tree, so "///" inside a string (the GpScaffolder templates) is not a hit.
// Cli/Templates is user-facing scaffolding and keeps its comments.
[Trait("Category", "Architecture")]
public sealed class EndpointProjectXmlDocTests
{
    private static readonly string[] XmlDocFreeRoots =
    [
        "src/Honua.Server",
        "src/Honua.Protocols",
        "src/Honua.Hosting",
        "src/Honua.Ai",
        "src/Honua.Import",
        "src/Honua.Geoprocessing",
    ];

    private const string ExcludedRoot = "src/Honua.Geoprocessing/Cli/Templates/";

    [ArchitectureTest]
    public void EndpointProjects_HaveNoXmlDocComments()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var offending = new List<string>();

        foreach (var root in XmlDocFreeRoots)
        {
            var rootPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, root.Replace('/', Path.DirectorySeparatorChar));
            Directory.Exists(rootPath).Should().BeTrue($"source root should exist: {root}");

            foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repositoryRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.StartsWith(ExcludedRoot, StringComparison.Ordinal))
                {
                    continue;
                }

                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                offending.AddRange(tree.GetRoot()
                    .DescendantTrivia()
                    .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                        || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    .Select(trivia => $"{relative}:{trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"));
            }
        }

        offending.Should().BeEmpty("application code in endpoint projects carries no XML doc comments; let names carry the meaning");
    }
}
