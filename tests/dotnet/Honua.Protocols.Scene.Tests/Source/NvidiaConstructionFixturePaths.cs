// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

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
