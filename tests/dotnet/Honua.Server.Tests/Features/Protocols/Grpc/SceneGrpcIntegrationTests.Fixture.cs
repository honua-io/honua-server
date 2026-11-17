// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed partial class SceneGrpcIntegrationTests
{
    // The configured scene files are immutable. Channels and database registrations
    // remain per case; xUnit serializes cases within this class.
    public sealed class SharedSceneHost : IAsyncLifetime
    {
        public WebAppFixture Fixture { get; }
        public string MissingContentRoot { get; }
        private readonly string _missingTilesetRoot;
        private readonly string _malformedTilesetRoot;
        private readonly string _oversizedTileRoot;

        public SharedSceneHost()
        {
            var fixtureRoot = ResolveFixtureRoot();
            MissingContentRoot = CreateMissingContentTilesetRoot();
            _missingTilesetRoot = CreateMissingTilesetRoot();
            _malformedTilesetRoot = CreateMalformedTilesetRoot();
            _oversizedTileRoot = CreateOversizedTileRoot();
            Fixture = new WebAppFixture()
                .ConfigureWebHost(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configBuilder) =>
                    {
                        configBuilder.AddInMemoryCollection(CreateSceneConfiguration(fixtureRoot));
                    });
                });
        }

        private Dictionary<string, string?> CreateSceneConfiguration(string fixtureRoot)
        {
            return new Dictionary<string, string?>
            {
                ["Scenes:Datasets:0:Id"] = FixtureSceneId,
                ["Scenes:Datasets:0:Name"] = "Honua Fixture Tileset",
                ["Scenes:Datasets:0:Description"] = "Static 3D Tiles fixture used by gRPC tests",
                ["Scenes:Datasets:0:AssetRoot"] = fixtureRoot,

                // A scene whose root node references a content uri that
                // does not exist on disk, to exercise the GetTile
                // content-resolution NotFound path.
                ["Scenes:Datasets:1:Id"] = MissingContentSceneId,
                ["Scenes:Datasets:1:Name"] = "Missing Content Tileset",
                ["Scenes:Datasets:1:AssetRoot"] = MissingContentRoot,

                // A scene whose asset root has NO tileset.json, exercising
                // the LoadCatalogAsync missing-tileset NotFound branch.
                ["Scenes:Datasets:2:Id"] = MissingTilesetSceneId,
                ["Scenes:Datasets:2:Name"] = "Missing Tileset",
                ["Scenes:Datasets:2:AssetRoot"] = _missingTilesetRoot,

                // A scene whose tileset.json deserializes to null (root:
                // null), exercising the LoadTilesetAsync Internal branch.
                ["Scenes:Datasets:3:Id"] = MalformedTilesetSceneId,
                ["Scenes:Datasets:3:Name"] = "Malformed Tileset",
                ["Scenes:Datasets:3:AssetRoot"] = _malformedTilesetRoot,

                // A scene whose root content uri points at a file larger
                // than MaxTileContentBytes, exercising the GetTile/
                // StreamTiles ResourceExhausted oversized-tile branch.
                ["Scenes:Datasets:4:Id"] = OversizedTileSceneId,
                ["Scenes:Datasets:4:Name"] = "Oversized Tile",
                ["Scenes:Datasets:4:AssetRoot"] = _oversizedTileRoot,
            };
        }

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public async Task DisposeAsync()
        {
            try
            {
                await Fixture.DisposeAsync();
            }
            finally
            {
                DeleteAssetRoots();
            }
        }

        private void DeleteAssetRoots()
        {
            foreach (var root in new[] { MissingContentRoot, _missingTilesetRoot, _malformedTilesetRoot, _oversizedTileRoot })
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort cleanup of the temp fixture root.
                }
            }
        }

        private static string CreateMissingContentTilesetRoot()
        {
            // Every Path.Combine call below only ever joins a temp directory with
            // fixed/generated relative literal segments, so no later segment can be
            // rooted and silently discard the one before it.
            var root = Path.Join(Path.GetTempPath(), "honua-grpc-missing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            const string tilesetJson = """
            {
              "asset": { "version": "1.1" },
              "geometricError": 100.0,
              "root": {
                "boundingVolume": { "region": [-1.31970, 0.69886, -1.31965, 0.69890, 0.0, 20.0] },
                "geometricError": 100.0,
                "refine": "REPLACE",
                "content": { "uri": "tiles/does-not-exist.b3dm" }
              }
            }
            """;
            File.WriteAllText(Path.Join(root, "tileset.json"), tilesetJson);
            return root;
        }

        private static string CreateMissingTilesetRoot()
        {
            // Second segment is a generated relative literal, so it can never be
            // rooted and silently discard Path.GetTempPath().
            var root = Path.Join(Path.GetTempPath(), "honua-grpc-no-tileset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static string CreateMalformedTilesetRoot()
        {
            // Both Path.Combine calls below only join a temp directory with a fixed
            // or generated relative literal segment, so neither later segment can be
            // rooted and silently discard the one before it.
            var root = Path.Join(Path.GetTempPath(), "honua-grpc-malformed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Join(root, "tileset.json"), "null");
            return root;
        }

        private static string CreateOversizedTileRoot()
        {
            // Every Path.Combine call below only joins a temp directory with fixed
            // or generated relative literal segments, so no later segment can be
            // rooted and silently discard the one before it.
            var root = Path.Join(Path.GetTempPath(), "honua-grpc-oversized-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            const string tilesetJson = """
            {
              "asset": { "version": "1.1" },
              "geometricError": 100.0,
              "root": {
                "boundingVolume": { "region": [-1.31970, 0.69886, -1.31965, 0.69890, 0.0, 20.0] },
                "geometricError": 100.0,
                "refine": "REPLACE",
                "content": { "uri": "tiles/oversized.b3dm" }
              }
            }
            """;
            File.WriteAllText(Path.Join(root, "tileset.json"), tilesetJson);

            var tilesDir = Path.Join(root, "tiles");
            Directory.CreateDirectory(tilesDir);
            // 256 MiB + 1 byte: one byte over MaxTileContentBytes. SetLength on a
            // fresh file produces a sparse extent on the platforms CI runs on, so no
            // bytes are actually written.
            const long oversized = (256L * 1024 * 1024) + 1;
            using (var fs = new FileStream(Path.Join(tilesDir, "oversized.b3dm"), FileMode.CreateNew, FileAccess.Write))
            {
                fs.SetLength(oversized);
            }

            return root;
        }

        private static string ResolveFixtureRoot()
        {
            var directory = AppContext.BaseDirectory;
            while (directory is not null)
            {
                // All later segments are fixed relative literals, so none can be
                // rooted and silently discard directory.
                var candidate = Path.Join(directory, "tests", "fixtures", "scenes", "fixture-tileset");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }

            throw new DirectoryNotFoundException(
                "Could not locate tests/fixtures/scenes/fixture-tileset from the test base directory.");
        }
    }
}
