// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Honua.Architecture.Tests;

// The library projects carry no XML doc comments. The clean-code check only looks at added
// lines, so this guard keeps the whole tree at zero. It reads documentation-comment trivia
// from the syntax tree, so "///" inside a string is not a hit.
[Trait("Category", "Architecture")]
public sealed class LibraryProjectXmlDocTests
{
    private static readonly string[] XmlDocFreeRoots =
    [
        "src/Honua.Analyzers",
        "src/Honua.Cloud",
        "src/Honua.Core",
        "src/Honua.Db",
        "src/Honua.Geocoding",
        "src/Honua.Geometry",
        "src/Honua.Io",
        "src/Honua.Jobs",
        "src/Honua.Plugins",
        "src/Honua.Routing",
        "src/Honua.Scene",
        "src/Honua.ServiceDefaults",
        "src/Honua.Worker.Gdal",
    ];

    [ArchitectureTest]
    public void LibraryProjects_HaveNoXmlDocComments()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var offending = XmlDocFreeRoots
            .SelectMany(root => SourceFiles(repositoryRoot, root))
            .SelectMany(file => XmlDocLocations(repositoryRoot, file))
            .ToList();

        offending.Should().BeEmpty("library code carries no XML doc comments; let names carry the meaning");
    }

    private static IEnumerable<string> SourceFiles(string repositoryRoot, string root)
    {
        var rootPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, root.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(rootPath).Should().BeTrue($"source root should exist: {root}");

        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal));
    }

    private static IEnumerable<string> XmlDocLocations(string repositoryRoot, string file)
    {
        var relative = Path.GetRelativePath(repositoryRoot, file).Replace(Path.DirectorySeparatorChar, '/');
        return CSharpSyntaxTree.ParseText(File.ReadAllText(file))
            .GetRoot()
            .DescendantTrivia()
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(trivia => $"{relative}:{trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
    }
}
