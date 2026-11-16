// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
            .SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
                .DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken)
                    && token.ValueText == "IConnectionMultiplexer")
                .Select(token => $"{Path.GetRelativePath(root, path)}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"))
            .ToArray();

        violations.Should().BeEmpty("source must use the native cache and durable database abstractions");
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
