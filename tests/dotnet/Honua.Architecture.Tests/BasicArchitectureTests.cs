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
        var projectRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "src")).Should().BeTrue($"src directory should exist in {projectRoot}");
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "tests")).Should().BeTrue($"tests directory should exist in {projectRoot}");
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "docs")).Should().BeTrue($"docs directory should exist in {projectRoot}");

        File.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "Honua.sln")).Should().BeTrue($"Solution file should exist in {projectRoot}");
        File.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "AGENTS.md")).Should().BeTrue($"Project instructions should exist in {projectRoot}");
    }

    [ArchitectureTest]
    public void ProjectFiles_ShouldHaveRequiredStructure()
    {
        var projectRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        // Verify src project directories
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "src", "Honua.Server")).Should().BeTrue($"Honua.Server project should exist in {projectRoot}");
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "src", "Honua.Core")).Should().BeTrue($"Honua.Core project should exist in {projectRoot}");
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "src", "Honua.Postgres")).Should().BeTrue($"Honua.Postgres project should exist in {projectRoot}");

        // Verify test project directories
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "tests", "dotnet", "Honua.TestKit")).Should().BeTrue($"Honua.TestKit project should exist in {projectRoot}");
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "tests", "dotnet", "Honua.Server.Tests")).Should().BeTrue($"Honua.Server.Tests project should exist in {projectRoot}");
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "tests", "dotnet", "Honua.Architecture.Tests")).Should().BeTrue($"Honua.Architecture.Tests project should exist in {projectRoot}");
    }
}
