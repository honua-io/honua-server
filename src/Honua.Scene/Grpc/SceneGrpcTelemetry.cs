// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Scene.Grpc;

/// <summary>
/// Telemetry constants for the scene/tile/elevation gRPC adapters. The tag and
/// operation names mirror the HTTP scene and elevation surfaces so traces stay
/// consistent across protocols (per the cross-cutting telemetry convention).
/// </summary>
internal static class SceneGrpcTelemetry
{
    public const string SceneIdTag = "honua.scene.id";
    public const string NodeIdTag = "honua.scene.node_id";
    public const string SceneCountTag = "honua.scene.count";
    public const string TileBytesTag = "honua.scene.tile_bytes";
    public const string TileCountTag = "honua.scene.tile_count";
    public const string SampleCountTag = "honua.elevation.sample_count";

    public const string ListScenesActivity = "honua.scene.list";
    public const string GetSceneActivity = "honua.scene.get";
    public const string GetTileActivity = "honua.tile.get";
    public const string StreamTilesActivity = "honua.tile.stream";
    public const string GetElevationActivity = "honua.elevation.value";
    public const string GetElevationProfileActivity = "honua.elevation.profile";

    public const string ListScenesOperation = "scene.list";
    public const string GetSceneOperation = "scene.get";
    public const string GetTileOperation = "tile.get";
    public const string StreamTilesOperation = "tile.stream";
    public const string GetElevationOperation = "elevation.value";
    public const string GetElevationProfileOperation = "elevation.profile";
}
