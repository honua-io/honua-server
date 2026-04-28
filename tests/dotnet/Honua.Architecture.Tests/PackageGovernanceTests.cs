// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces NuGet package version governance.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class PackageGovernanceTests
{
    [ArchitectureTest]
    public void DirectoryPackagesProps_ShouldEnableCentralPackageManagement()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var packagesPath = Path.Combine(repositoryRoot, "Directory.Packages.props");

        File.Exists(packagesPath).Should().BeTrue("NuGet package versions must be declared once at the repository root");

        var document = XDocument.Load(packagesPath);
        var centrallyManaged = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "ManagePackageVersionsCentrally")
            ?.Value;
        var overridesEnabled = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "CentralPackageVersionOverrideEnabled")
            ?.Value;

        centrallyManaged.Should().Be("true", "package versions should be governed by Directory.Packages.props");
        overridesEnabled.Should().Be("false", "project-local VersionOverride metadata would bypass package governance");
    }

    [ArchitectureTest]
    public void ProjectPackageReferences_ShouldNotDeclareInlineVersions()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var violations = EnumerateProjectFiles(repositoryRoot)
            .SelectMany(projectPath => FindInlinePackageVersions(repositoryRoot, projectPath))
            .ToArray();

        violations.Should().BeEmpty(
            "PackageReference items should declare only package identity and project-local metadata; versions belong in Directory.Packages.props");
    }

    private static IEnumerable<string> EnumerateProjectFiles(string repositoryRoot)
    {
        return Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedBuildPath(path))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static bool IsGeneratedBuildPath(string path)
    {
        return path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindInlinePackageVersions(string repositoryRoot, string projectPath)
    {
        var document = XDocument.Load(projectPath, LoadOptions.SetLineInfo);

        foreach (var packageReference in document.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
        {
            var packageId = packageReference.Attribute("Include")?.Value
                ?? packageReference.Attribute("Update")?.Value
                ?? "<unknown>";

            if (packageReference.Attribute("Version") is not null)
            {
                yield return FormatViolation(repositoryRoot, projectPath, packageReference, packageId, "Version attribute");
            }

            if (packageReference.Attribute("VersionOverride") is not null)
            {
                yield return FormatViolation(repositoryRoot, projectPath, packageReference, packageId, "VersionOverride attribute");
            }

            if (packageReference.Elements().Any(element => element.Name.LocalName == "Version"))
            {
                yield return FormatViolation(repositoryRoot, projectPath, packageReference, packageId, "Version element");
            }
        }
    }

    private static string FormatViolation(
        string repositoryRoot,
        string projectPath,
        XElement packageReference,
        string packageId,
        string versionSource)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, projectPath);
        var lineInfo = (IXmlLineInfo)packageReference;
        var location = lineInfo.HasLineInfo()
            ? $"{relativePath}:{lineInfo.LineNumber}"
            : relativePath;

        return $"{location} PackageReference '{packageId}' declares inline {versionSource}";
    }
}
