// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class SceneIsolationTests
{
    private static readonly string[] BannedProjectReferences =
    {
        "Honua.Server",
    };

    [ArchitectureTest]
    public void HonuaSceneCsproj_ShouldNotReference_HonuaServer()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sceneCsproj = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "src",
            "Honua.Scene",
            "Honua.Scene.csproj");

        File.Exists(sceneCsproj).Should().BeTrue(
            "the Honua.Scene csproj must exist at the canonical carve path: {0}",
            sceneCsproj);

        var projectReferences = ArchitectureTestHelpers.DirectProjectReferenceNames(sceneCsproj);

        var offending = projectReferences
            .Where(name => BannedProjectReferences.Contains(name, StringComparer.Ordinal))
            .ToList();

        offending
            .Should()
            .BeEmpty(
                "Honua.Scene was carved out of Honua.Core/Honua.Server so the scene surface " +
                "can be referenced by the Protocols.Scene adapter and the Server host without " +
                "pulling Honua.Server transitively. A back-reference to Honua.Server would " +
                "re-introduce the cycle the carve was designed to break. Move the dependency " +
                "into Honua.Core / Honua.Core.Abstractions / Honua.ServiceDefaults, or keep the " +
                "composition wiring in the Server host (as with SceneGenerationServiceCollectionExtensions).");
    }
}
