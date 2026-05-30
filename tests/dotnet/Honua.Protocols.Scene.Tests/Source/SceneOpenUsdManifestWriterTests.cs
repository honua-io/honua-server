// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Scene;

namespace Honua.Server.Tests.Features.Protocols.Scene;

public sealed class SceneOpenUsdManifestWriterTests
{
    [Fact]
    public void Write_SameInput_ProducesStableContentAndEtag()
    {
        var first = OpenUsdSceneManifestWriter.Write(BuildInput());
        var second = OpenUsdSceneManifestWriter.Write(BuildInput());

        first.Content.Should().Be(second.Content);
        first.ETag.Should().Be(second.ETag);
        first.Content.Should().Contain("honua:sourceFormat = \"OGC 3D Tiles\"");
        first.Content.Should().Contain("honua:nativeUsdGeometry = \"false\"");
        first.Content.Should().Contain("honua:projectId = \"project-1\"");
        first.Content.Should().Contain("Observation_obs_001");
        first.Content.Should().NotContain("evidenceUris");
    }

    [Fact]
    public void Write_EscapesUsdStringMetadata()
    {
        var input = BuildInput() with
        {
            SceneName = "Quoted \"Scene\"",
            SceneDescription = "Line one\nLine two"
        };

        var manifest = OpenUsdSceneManifestWriter.Write(input);

        manifest.Content.Should().Contain("honua:name = \"Quoted \\\"Scene\\\"\"");
        manifest.Content.Should().Contain("honua:description = \"Line one\\nLine two\"");
    }

    private static OpenUsdSceneManifestInput BuildInput()
        => new(
            SceneId: "scene-1",
            SceneName: "Scene One",
            SceneDescription: "Test scene",
            IsProtected: false,
            Crs: "EPSG:4979",
            Extent: new OpenUsdSceneExtent(-1, 1, 2, 3, 0, 10, "test"),
            Tileset: new OpenUsdTilesetManifestInput
            {
                Asset = new OpenUsdTilesetAsset
                {
                    Version = "1.1",
                    TilesetVersion = "tileset-1",
                    Generator = "test"
                },
                Extras = new OpenUsdTilesetExtras
                {
                    LayerKind = "structure",
                    LayerId = "scene-1",
                    ProjectMeta = new OpenUsdProjectMeta
                    {
                        Id = "project-1",
                        Name = "Project One",
                        WorkPackages =
                        [
                            new OpenUsdWorkPackage { Id = "wp-1", Name = "Foundation" }
                        ]
                    }
                }
            },
            Observations: new OpenUsdObservationsManifestInput
            {
                SchemaVersion = "1.0",
                SidecarUri = "observations.json",
                SidecarUrl = "https://example.test/scenes/scene-1/observations.json",
                Observations =
                [
                    new OpenUsdObservation
                    {
                        Id = "obs-001",
                        Kind = "progress",
                        Status = "open",
                        Severity = "info",
                        Longitude = -1,
                        Latitude = 1,
                        EvidenceCount = 2
                    }
                ]
            },
            ManifestUrl: "https://example.test/scenes/scene-1/exports/openusd/stage.usda",
            TilesetUrl: "https://example.test/scenes/scene-1/tileset.json",
            ObservationsUrl: "https://example.test/scenes/scene-1/observations.json",
            AccessEnvelopeUrl: null);
}
