// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// A throwing default body on a Metadata v2 interface method fails only at runtime, when an implementation
// forgets the override; an abstract member makes the compiler catch it. Reads source because reflection
// cannot tell an abstract member from a throwing default.
[Trait("Category", "Architecture")]
public sealed class ThrowingDefaultInterfaceMethodRatchetTests
{
    private static readonly string[] _v2InterfaceFiles =
    {
        "src/Honua.Core/Features/Validation/Abstractions/IResourceValidator.cs",
        "src/Honua.Core/Features/Query/IQueryProcessor.cs",
        "src/Honua.Core/Features/Query/IQueryParameterAdapter.cs",
        "src/Honua.Core/Features/Edit/IEditParameterAdapter.cs",
    };

    // Interface file -> why its throwing default is deferred, and where the follow-up is tracked.
    private static readonly Dictionary<string, string> _deferredThrowingDefaults = new(StringComparer.Ordinal);

    private static readonly Regex _throwingDefault = new(
        @"throw\s+new\s+NotSupportedException\b",
        RegexOptions.None,
        TimeSpan.FromSeconds(1));

    [ArchitectureTest]
    public void V2_InterfaceMethods_ShouldNotShipThrowingDefaults_UnlessDeferred()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var violations = _v2InterfaceFiles
            .Where(relativePath => !_deferredThrowingDefaults.ContainsKey(relativePath))
            .Where(relativePath => _throwingDefault.IsMatch(ReadInterfaceSource(repositoryRoot, relativePath)))
            .ToList();

        violations.Should().BeEmpty(
            "a v2 interface method must be abstract; to defer one, add its file to _deferredThrowingDefaults with the reason");
    }

    private static string ReadInterfaceSource(string repositoryRoot, string relativePath)
    {
        var absolutePath = ArchitectureTestHelpers.CombinePath(repositoryRoot, relativePath);
        File.Exists(absolutePath).Should().BeTrue($"'{relativePath}' must exist; update _v2InterfaceFiles if it moved");
        return File.ReadAllText(absolutePath);
    }
}
