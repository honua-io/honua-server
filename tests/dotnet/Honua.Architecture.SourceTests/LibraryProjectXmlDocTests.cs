// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Honua.Architecture.Tests;

// The library projects and the tests carry no XML doc comments. The clean-code check only looks at added
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
    public void SourceFiles_UnderLibraryRoots_HaveNoXmlDocComments()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var offending = XmlDocFreeRoots
            .SelectMany(root => SourceFiles(repositoryRoot, root))
            .SelectMany(file => XmlDocLocations(repositoryRoot, file))
            .ToList();

        offending.Should().BeEmpty("library code carries no XML doc comments; let names carry the meaning");
    }

    [ArchitectureTest]
    public void SourceFiles_UnderTests_HaveNoXmlDocComments()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var offending = SourceFiles(repositoryRoot, "tests")
            .SelectMany(file => XmlDocLocations(repositoryRoot, file))
            .ToList();

        offending.Should().BeEmpty("test code carries no XML doc comments; let test names carry the meaning");
    }

    [ArchitectureTest]
    public void SourceFiles_UnderLibrariesAndTests_HaveNoTicketOrHistoryComments()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var forbidden = new Regex(
            @"(?<![\w/])(?:honua-server#|#)[1-9]\d{2,4}(?!\w)|\bIssue\s+#[1-9]\d*\b|\b(?:BH\d+-\d+|bug hunt|pre-fix|before this (?:fix|change)|previous (?:code|implementation)|review finding)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var offending = XmlDocFreeRoots.Append("tests")
            .SelectMany(root => SourceFiles(repositoryRoot, root))
            .SelectMany(file => SourceTrivia(File.ReadAllText(file))
                .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                .Where(trivia => forbidden.IsMatch(trivia.ToString()))
                .Select(trivia => $"{Path.GetRelativePath(repositoryRoot, file)}:{trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"))
            .ToList();

        offending.Should().BeEmpty("comments describe current contracts; ticket and change history belong in version control");
    }

    [Theory]
    [InlineData("#if NET11_0_OR_GREATER\n// Issue #7872\n#endif", 1)]
    [InlineData("#if !HONUA_EXCLUDE_AWS\n// Issue #7872\n#endif", 1)]
    [InlineData("#if FIRST && SECOND\n// Issue #7872\n#endif", 1)]
    [InlineData("#if FIRST\n#else\n#if SECOND\n// Issue #7872\n#endif\n#endif", 1)]
    [InlineData("#if FIRST\n#elif SECOND\n// Issue #7872\n#endif", 1)]
    [InlineData("#if FIRST\nconst string Value = \"// Issue #7872\";\n#endif", 0)]
    [InlineData("#if FIRST\nconst string Value = \"\"\"// Issue #7872\"\"\";\n#endif", 0)]
    public void SourceTrivia_InspectsConditionalCommentsAndPreservesStrings(string source, int expectedComments)
    {
        SourceTrivia(source)
            .Count(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            .Should().Be(expectedComments);
    }

    private static IEnumerable<SyntaxTrivia> SourceTrivia(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var symbols = root.DescendantTrivia()
            .Select(trivia => trivia.GetStructure() switch
            {
                IfDirectiveTriviaSyntax directive => directive.Condition,
                ElifDirectiveTriviaSyntax directive => directive.Condition,
                _ => null
            })
            .OfType<ExpressionSyntax>()
            .SelectMany(condition => condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(identifier => identifier.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal);
        var options = new List<CSharpParseOptions> { CSharpParseOptions.Default };
        foreach (var symbol in symbols)
        {
            options.AddRange(options
                .Select(option => option.WithPreprocessorSymbols(option.PreprocessorSymbolNames.Append(symbol)))
                .ToArray());
        }

        return options.Skip(1)
            .Select(option => CSharpSyntaxTree.ParseText(source, option).GetRoot())
            .Prepend(root)
            .SelectMany(node => node.DescendantTrivia())
            .DistinctBy(trivia => (trivia.SpanStart, trivia.RawKind));
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
        return SourceTrivia(File.ReadAllText(file))
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(trivia => $"{relative}:{trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
    }
}
