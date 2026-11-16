// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

public sealed partial class RemovedDeployTargetAbsenceTests
{
    private static readonly string[] ScannedRoots = ["src", "docker"];
    private static readonly string[] ProjectRoots = [".github", "docker", "eng", "scripts", "src", "tests"];
    private static readonly string[] ProjectFilePatterns = ["*.csproj", "*.props", "*.targets"];

    [ArchitectureTest]
    public void Repository_ShouldNotShipAzureFunctionsImageAssets()
    {
        var docker = Path.Join(ArchitectureTestHelpers.ResolveRepositoryRoot(), "docker");

        Directory.EnumerateFiles(docker, "Dockerfile.functions*").Should().BeEmpty();
        Directory.Exists(Path.Join(docker, "cloud", "azure-functions")).Should().BeFalse();
    }

    [ArchitectureTest]
    public void Source_ShouldNotNameAzureFunctionsOrContainerAppsFilesOrTypes()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var files = ScannedRoots
            .SelectMany(root => Directory.EnumerateFiles(Path.Join(repositoryRoot, root), "*", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path))
            .ToArray();

        files.Where(path => AzureTargetName().IsMatch(Path.GetRelativePath(repositoryRoot, path)))
            .Should().BeEmpty("no file under src/ or docker/ may be named for an Azure Functions or Container Apps deploy target");
        files.Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => AzureTargetType().IsMatch(File.ReadAllText(path)))
            .Should().BeEmpty("no type under src/ may be named for an Azure Functions or Container Apps deploy target");
    }

    [ArchitectureTest]
    public void Source_ShouldNotNameSelfHostedRollingProxyFilesOrTypes()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Join(repositoryRoot, "src"), "*", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToArray();

        files.Where(path => RollingProxyName().IsMatch(Path.GetFileName(path)))
            .Should().BeEmpty("no file under src/ may be named for the self-hosted rolling proxy deploy target");
        files.Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => RollingProxyName().IsMatch(File.ReadAllText(path)))
            .Should().BeEmpty("no code under src/ may name the self-hosted rolling proxy deploy target");
    }

    [ArchitectureTest]
    public void Projects_ShouldNotReferenceTheYarpReverseProxyPackage()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var projectFiles = ProjectFilePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(repositoryRoot, pattern, SearchOption.TopDirectoryOnly)
                .Concat(ProjectRoots.SelectMany(root => Directory.EnumerateFiles(Path.Join(repositoryRoot, root), pattern, SearchOption.AllDirectories))))
            .Where(path => !IsBuildOutput(path))
            .ToArray();

        projectFiles.Should().NotBeEmpty();
        projectFiles.Where(path => File.ReadAllText(path).Contains("Yarp.ReverseProxy", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("the embedded reverse proxy package may not be referenced by any project or props file");
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [GeneratedRegex(@"Azure[-_]?(Functions|Container[-_]?Apps)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AzureTargetName();

    [GeneratedRegex(@"\b(class|record|struct|interface|enum)\s+I?Azure(Functions|ContainerApps)\w*", RegexOptions.CultureInvariant)]
    private static partial Regex AzureTargetType();

    [GeneratedRegex(@"YarpRolling|SelfHostedRolling|SelfHostedProxy|SelfHostedDeploy", RegexOptions.CultureInvariant)]
    private static partial Regex RollingProxyName();
}
