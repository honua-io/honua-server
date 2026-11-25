// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class ResponseClearCorsGuardTests
{
    private const string AllowedRelativePath = "src/Honua.Hosting/Features/Security/HttpResponseClearHelper.cs";

    private static readonly Regex DirectResponseClearRegex = new(
        @"\.Response\.Clear\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void SourceFiles_DoNotCallResponseClearDirectly()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var allowedFullPath = ArchitectureTestHelpers.CombinePath(repoRoot, AllowedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(allowedFullPath).Should().BeTrue($"expected CORS-preserving clear helper at {AllowedRelativePath}");

        var sourceRoot = ArchitectureTestHelpers.CombinePath(repoRoot, "src");
        var offending = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (IsBuildOutput(relativePath) || string.Equals(relativePath, AllowedRelativePath, StringComparison.Ordinal))
            {
                continue;
            }

            var content = StripComments(File.ReadAllText(file));
            if (DirectResponseClearRegex.IsMatch(content))
            {
                offending.Add(relativePath);
            }
        }

        offending.Sort(StringComparer.Ordinal);
        offending.Should().BeEmpty(
            "calling HttpResponse.Clear() directly strips CORS response headers (Access-Control-* and Vary); "
            + "use HttpResponseClearHelper.ClearPreservingCorsHeaders(context.Response) instead. Offending files: "
            + string.Join("; ", offending));
    }

    private static bool IsBuildOutput(string relativePath)
        => relativePath.Contains("/bin/", StringComparison.Ordinal)
            || relativePath.Contains("/obj/", StringComparison.Ordinal);

    private static string StripComments(string source)
    {
        var noBlockComments = Regex.Replace(source, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, "//[^\n]*", string.Empty);
    }
}
