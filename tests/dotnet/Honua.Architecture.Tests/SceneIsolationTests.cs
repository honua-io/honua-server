// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the Honua.Scene isolation contract: the 3D scene subsystem (3D
/// Tiles generation, scene registry, publishing executor, and the gRPC
/// scene/tile/elevation services) is carved out of Honua.Core and Honua.Server
/// so the protocol adapter (Honua.Protocols.Scene) and the Server host can
/// consume it without it living inside the Core or Server graphs. The carve
/// only holds if Honua.Scene itself does not back-reference Honua.Server.
/// </summary>
/// <remarks>
/// Pinning the ProjectReference closure at the csproj layer (rather than
/// scanning IL) catches the policy violation at PR time. Scene *domain* records
/// intentionally remain in Honua.Core.Abstractions because the MetadataV2 graph
/// references them; moving them into Honua.Scene would invert that edge.
/// </remarks>
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
        var sceneCsproj = Path.Combine(
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
