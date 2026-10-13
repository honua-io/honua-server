// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Keeps inexpensive source guards independent of executable runtime/test assembly discovery.
/// </summary>
public sealed class SourceArchitectureIsolationTests
{
    [ArchitectureTest]
    public void SourceGuards_ReferenceOnlyLightweightTestAbstractions()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var pending = new Stack<string>();
        pending.Push(Path.Join(root, "tests/dotnet/Honua.Architecture.SourceTests/Honua.Architecture.SourceTests.csproj"));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new[] { "Honua.Architecture.SourceTests", "Honua.TestKit.Abstractions" };
        var packages = new[]
        {
            "Microsoft.NET.Test.Sdk", "Microsoft.CodeAnalysis.CSharp", "FluentAssertions",
            "xunit", "xunit.abstractions", "xunit.runner.visualstudio"
        };
        while (pending.TryPop(out var project))
        {
            if (!seen.Add(project))
            {
                continue;
            }

            Assert.Contains(Path.GetFileNameWithoutExtension(project), allowed);
            var document = XDocument.Load(project);
            Assert.Empty(document.Descendants("Import"));
            foreach (var package in document.Descendants("PackageReference"))
            {
                Assert.Contains(Assert.IsType<string>(package.Attribute("Include")?.Value), packages);
            }
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = Assert.IsType<string>(reference.Attribute("Include")?.Value);
                pending.Push(Path.GetFullPath(Path.Join(Path.GetDirectoryName(project), include.Replace('\\', '/'))));
            }
        }
    }

    [ArchitectureTest]
    public void MovedGuardClasses_HaveOneCanonicalSourceOwner()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Join(root,
            "tests/dotnet/Honua.Architecture.SourceTests/moved-tests.json")));
        var sourceOwners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var directory in new[] { "Honua.Architecture.Tests", "Honua.Architecture.SourceTests" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Join(root, "tests/dotnet", directory), "*.cs", SearchOption.AllDirectories)
                         .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
            {
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
                foreach (var declaration in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (declaration.Parent is not BaseNamespaceDeclarationSyntax testNamespace)
                    {
                        continue;
                    }

                    var name = $"{testNamespace.Name}.{declaration.Identifier.ValueText}";
                    if (!sourceOwners.TryGetValue(name, out var owners))
                    {
                        owners = [];
                        sourceOwners.Add(name, owners);
                    }

                    owners.Add(path);
                }
            }
        }

        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in inventory.RootElement.GetProperty("files").EnumerateArray())
        {
            Assert.False(File.Exists(Path.Join(root, file.GetProperty("original").GetString())));
            var current = Path.Join(root, file.GetProperty("current").GetString());
            Assert.True(File.Exists(current), current);
            var source = File.ReadAllText(current);
            var testNamespace = file.GetProperty("namespace").GetString();
            Assert.Contains($"namespace {testNamespace};", source, StringComparison.Ordinal);
            foreach (var name in file.GetProperty("classes").EnumerateArray())
            {
                var identity = $"{testNamespace}.{name.GetString()}";
                Assert.True(classes.Add(identity));
                Assert.Equal(current, Assert.Single(sourceOwners[identity]));
            }
        }

        var collector = XDocument.Load(Path.Join(root,
            "tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj"));
        Assert.DoesNotContain(collector.Descendants("Compile"), reference =>
            reference.Attribute("Include")?.Value.Contains("SourceTests", StringComparison.Ordinal) == true &&
            !reference.Attribute("Include")!.Value.EndsWith("ArchitectureTestHelpers.Source.cs", StringComparison.Ordinal));
    }
}
