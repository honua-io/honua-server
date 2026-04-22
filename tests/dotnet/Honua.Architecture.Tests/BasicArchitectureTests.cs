// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Basic architecture tests to validate project structure
/// Reference: AGENTS.md Architecture Guardrails
/// </summary>
[Trait("Category", "Architecture")]
public class BasicArchitectureTests
{
    [ArchitectureTest]
    public void ProjectStructure_ShouldBeCorrect()
    {
        // Find project root by looking for solution file
        var currentDir = Directory.GetCurrentDirectory();
        var projectRoot = FindProjectRoot(currentDir);

        Directory.Exists(Path.Combine(projectRoot, "src")).Should().BeTrue($"src directory should exist in {projectRoot}");
        Directory.Exists(Path.Combine(projectRoot, "tests")).Should().BeTrue($"tests directory should exist in {projectRoot}");
        Directory.Exists(Path.Combine(projectRoot, "docs")).Should().BeTrue($"docs directory should exist in {projectRoot}");

        File.Exists(Path.Combine(projectRoot, "Honua.sln")).Should().BeTrue($"Solution file should exist in {projectRoot}");
        File.Exists(Path.Combine(projectRoot, "CLAUDE.md")).Should().BeTrue($"Project instructions should exist in {projectRoot}");
    }

    private static string FindProjectRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException($"Could not find project root starting from {startPath}");
    }

    [ArchitectureTest]
    public void ProjectFiles_ShouldHaveRequiredStructure()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var projectRoot = FindProjectRoot(currentDir);

        // Verify src project directories
        Directory.Exists(Path.Combine(projectRoot, "src", "Honua.Server")).Should().BeTrue($"Honua.Server project should exist in {projectRoot}");
        Directory.Exists(Path.Combine(projectRoot, "src", "Honua.Core")).Should().BeTrue($"Honua.Core project should exist in {projectRoot}");
        Directory.Exists(Path.Combine(projectRoot, "src", "Honua.Postgres")).Should().BeTrue($"Honua.Postgres project should exist in {projectRoot}");

        // Verify test project directories
        Directory.Exists(Path.Combine(projectRoot, "tests", "dotnet", "Honua.TestKit")).Should().BeTrue($"Honua.TestKit project should exist in {projectRoot}");
        Directory.Exists(Path.Combine(projectRoot, "tests", "dotnet", "Honua.Server.Tests")).Should().BeTrue($"Honua.Server.Tests project should exist in {projectRoot}");
        Directory.Exists(Path.Combine(projectRoot, "tests", "dotnet", "Honua.Architecture.Tests")).Should().BeTrue($"Honua.Architecture.Tests project should exist in {projectRoot}");
    }
}
