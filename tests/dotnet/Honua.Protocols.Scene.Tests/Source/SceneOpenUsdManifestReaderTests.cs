// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Protocols.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Unit tests for the OpenUSD scene manifest reader. The reader maps a hosted
/// scene's tileset + observation sidecar into the manifest input model; these
/// tests isolate URL escaping of sidecar paths and CRS validation without a
/// web host.
/// </summary>
[Protocol(TestProtocols.Scene)]
public sealed class SceneOpenUsdManifestReaderTests
{
    [UnitTest]
    public async Task ReadAsync_ObservationSidecarWithSpaces_EmitsEscapedManifestUrls()
    {
        var root = CreateTempSceneRoot(
            """
            {
              "asset": { "version": "1.1" },
              "extras": {
                "bounds": { "west": -1, "south": 1, "east": 2, "north": 3 },
                "observationsLayer": { "sidecarUri": "project data/observations data.json" }
              }
            }
            """,
            ("project data/observations data.json", """
            {
              "schemaVersion": "1.0",
              "observations": []
            }
            """));

        try
        {
            var input = await OpenUsdSceneManifestReader.ReadAsync(
                CreateDataset(root),
                record: null,
                manifestUrl: "https://example.test/scenes/scene-1/exports/openusd/stage.usda",
                tilesetUrl: "https://example.test/scenes/scene-1/tileset.json",
                accessEnvelopeUrl: null,
                CancellationToken.None);

            var expectedUrl = "https://example.test/scenes/scene-1/project%20data/observations%20data.json";
            input.ObservationsUrl.Should().Be(expectedUrl);
            input.Observations!.SidecarUrl.Should().Be(expectedUrl);

            var manifest = OpenUsdSceneManifestWriter.Write(input);
            manifest.Content.Should().Contain($"honua:observationsUrl = \"{expectedUrl}\"");
            manifest.Content.Should().Contain($"honua:sidecarUrl = \"{expectedUrl}\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("EPSG:3857")]
    [InlineData("http://www.opengis.net/def/crs/EPSG/0/3857")]
    [Trait("Category", "Unit")]
    public async Task ReadAsync_WithProjectedSceneCrs_RejectsManifest(string crs)
    {
        var root = CreateTempSceneRoot(
            """
            {
              "asset": { "version": "1.1" },
              "extras": {
                "bounds": { "west": -1, "south": 1, "east": 2, "north": 3 }
              }
            }
            """);

        try
        {
            var exception = await Assert.ThrowsAsync<OpenUsdManifestValidationException>(() =>
                OpenUsdSceneManifestReader.ReadAsync(
                    CreateDataset(root),
                    new OpenUsdSceneManifestReader.SceneDatasetRecordView(Extent: null, Crs: crs),
                    manifestUrl: "https://example.test/scenes/scene-1/exports/openusd/stage.usda",
                    tilesetUrl: "https://example.test/scenes/scene-1/tileset.json",
                    accessEnvelopeUrl: null,
                    CancellationToken.None));

            exception.Reason.Should().Be("scene_crs_unsupported");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SceneDataset CreateDataset(string root) => new()
    {
        Id = "scene-1",
        Name = "Scene One",
        AssetRoot = root,
        TilesetFileName = "tileset.json"
    };

    private static string CreateTempSceneRoot(
        string tilesetJson,
        params (string RelativePath, string Content)[] additionalFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "honua-openusd-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tileset.json"), tilesetJson);

        foreach (var (relativePath, content) in additionalFiles)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return root;
    }
}
