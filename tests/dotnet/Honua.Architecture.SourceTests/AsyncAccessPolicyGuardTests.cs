// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class AsyncAccessPolicyGuardTests
{
    private const string AllowedRelativePath = "src/Honua.Hosting/Features/Authentication/AccessPolicyHelpers.cs";

    private static readonly Regex SyncAccessPolicyCallRegex = new(
        @"\bAccessPolicyHelpers\.(EvaluateResourceAccess|RequireResourceAccess|IsResourceAccessible|RequireServiceAccess|EvaluateServiceAccess|RequireAnyResourceAccess)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void SourceFiles_DoNotCallSyncAccessPolicyHelpers()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var allowedFullPath = ArchitectureTestHelpers.CombinePath(repoRoot, AllowedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(allowedFullPath).Should().BeTrue($"expected AccessPolicyHelpers at {AllowedRelativePath}");

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
            if (SyncAccessPolicyCallRegex.IsMatch(content))
            {
                offending.Add(relativePath);
            }
        }

        offending.Sort(StringComparer.Ordinal);
        offending.Should().BeEmpty(
            "request handlers and services must call the async AccessPolicyHelpers.*Async methods rather than "
            + "synchronous overloads to avoid sync-over-async blocking during access evaluation. Offending files: "
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
