// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Honua.Architecture.Tests;

public sealed class RetiredCacheDependencyTests
{
    private static readonly string[] _sourceDirectories = ["src", "tests"];

    [ArchitectureTest]
    public void BuildDeclarations_ShouldNotReferenceRetiredCachePackages()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var violations = RepositoryFiles(root)
            .Where(path => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets")
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(element => element.Name.LocalName is
                    "PackageReference" or "GlobalPackageReference" or "PackageVersion" or "PackageDownload")
                .SelectMany(element => element.Attributes()
                    .Where(attribute => attribute.Name.LocalName is "Include" or "Update"))
                .Where(attribute => attribute.Value.Contains("Redis", StringComparison.OrdinalIgnoreCase))
                .Select(attribute => $"{Path.GetRelativePath(root, path)}: {attribute.Value}"))
            .ToArray();

        violations.Should().BeEmpty("retired cache packages must not return through project or central build declarations");
    }

    [ArchitectureTest]
    public void Source_ShouldNotUseRetiredConnectionMultiplexer()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var violations = _sourceDirectories
            .SelectMany(directory => RepositoryFiles(Path.Join(root, directory)))
            .Where(path => Path.GetExtension(path) == ".cs")
            .Where(path => UsesRetiredConnectionMultiplexer(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        violations.Should().BeEmpty("source must use the native cache and durable database abstractions");
    }

    [Theory]
    [InlineData("class C { IConnectionMultiplexer cache; }", true)]
    [InlineData("#if NET11_0_OR_GREATER\nclass C { IConnectionMultiplexer cache; }\n#endif", true)]
    [InlineData("#if false\n#if OPTIONAL\nclass C { IConnectionMultiplexer cache; }\n#endif\n#endif", true)]
    [InlineData("#if true\nclass C {}\n#else\nclass C { IConnectionMultiplexer cache; }\n#endif", true)]
    [InlineData("#if OPTIONAL\nclass C { @IConnectionMultiplexer cache; }\n#endif", true)]
    [InlineData("// IConnectionMultiplexer\nclass C { string Name = \"IConnectionMultiplexer\"; }", false)]
    [InlineData("#if OPTIONAL\n// IConnectionMultiplexer\nclass C { string Name = \"IConnectionMultiplexer\"; }\n#endif", false)]
    public void ConnectionGuard_ConditionalSource_InspectsIdentifiersOnly(string source, bool expected)
    {
        UsesRetiredConnectionMultiplexer(source).Should().Be(expected);
    }

    private static bool UsesRetiredConnectionMultiplexer(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var disabledTokens = root.DescendantTrivia()
            .Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            .SelectMany(trivia => SyntaxFactory.ParseTokens(trivia.ToFullString()));

        return root.DescendantTokens().Concat(disabledTokens)
            .Any(token => token.IsKind(SyntaxKind.IdentifierToken)
                && token.ValueText == "IConnectionMultiplexer");
    }

    private static IEnumerable<string> RepositoryFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory)
            .Where(path => Path.GetFileName(path) is not ("bin" or "obj" or ".git" or "node_modules" or ".venv" or "artifacts")))
        {
            foreach (var file in RepositoryFiles(child))
            {
                yield return file;
            }
        }
    }
}
