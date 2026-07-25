// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards integration-test runner settings that materially affect shared Postgres fixtures.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class TestInfrastructureConfigurationTests
{
    [ArchitectureTest]
    public void ServerIntegrationRunner_ShouldBoundCollectionParallelism()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var configurationPath = ArchitectureTestHelpers.CombinePath(
            repoRoot,
            "tests",
            "dotnet",
            "Honua.Server.Tests",
            "xunit.runner.json");

        using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
        var root = document.RootElement;

        root.GetProperty("parallelizeTestCollections").GetBoolean().Should().BeTrue();
        root.GetProperty("maxParallelThreads").GetInt32().Should().Be(8,
            "the DB-heavy server test assembly must cap concurrent collections so schema setup "
            + "does not amplify the honua-server#1568 catalog-lock deadlock window");
    }
}
