// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// Behind a CDN a wrong `public` is a data leak, so the header is written in one place that also checks who is asking.
[Trait("Category", "Architecture")]
public sealed partial class SharedCacheSingleWriterGuardTests
{
    private static readonly string[] PermittedWriters =
    [
        Path.Combine("src", "Honua.Hosting", "Features", "SharedCaching") + Path.DirectorySeparatorChar,
        Path.Combine("src", "Honua.Core", "Core", "Features", "Infrastructure", "Domain", "StoredObjectCachePolicy.cs"),
    ];

    [ArchitectureTest]
    public void OnlyTheSharedCachePolicy_WritesAPublicCacheControl()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(repoRoot, "src");

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(repoRoot, file))
            .Where(relative => !PermittedWriters.Any(permitted => relative.StartsWith(permitted, StringComparison.Ordinal)))
            .Where(relative => SharedCacheableDirective().IsMatch(File.ReadAllText(Path.Combine(repoRoot, relative))))
            .ToArray();

        offenders.Should().BeEmpty(
            "a shared-cacheable Cache-Control is written only by SharedCacheResponse; declare the route with WithSharedCacheClass instead");
    }

    [GeneratedRegex(@"""[^""\n]*\bpublic\s*,\s*(max-age|s-maxage|immutable)|s-maxage\s*=|\bPublic\s*=\s*true\b[^;\n]*MaxAge|SharedMaxAge\s*=(?!\s*null)")]
    private static partial Regex SharedCacheableDirective();
}
