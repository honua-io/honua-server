// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Locates the NVIDIA construction demo fixture directory
/// (<c>tests/fixtures/scenes/nvidia-construction</c>) by walking up from the
/// test runner's base directory to the repo root. The fixture backs both the
/// 3D structure scene (<see cref="MainSceneId"/>) and the observation pins
/// layer (<see cref="ObsSceneId"/>); they share <c>AssetRoot</c> and differ
/// only in <c>TilesetFileName</c>.
/// </summary>
internal static class NvidiaConstructionFixturePaths
{
    public const string MainSceneId = "nvidia-construction";
    public const string ObsSceneId = "nvidia-construction-obs";
    public const string MainTilesetFileName = "tileset.json";
    public const string ObsTilesetFileName = "obs-tileset.json";
    public const string ObservationsSidecarFileName = "observations.json";
    public const string StructureTileRelativePath = "tiles/structure.b3dm";
    public const string ObsPinTileRelativePath = "tiles/obs-pin.b3dm";

    public static string ResolveFixtureRoot() => SceneFixtureRoots.Resolve("nvidia-construction");
}
