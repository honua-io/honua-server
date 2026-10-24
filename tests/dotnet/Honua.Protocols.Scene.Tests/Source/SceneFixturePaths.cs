// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Locates the canonical 3D Tiles fixture directory (<c>tests/fixtures/scenes/fixture-tileset</c>)
/// from the test runner's base directory by walking up to the repo root.
/// </summary>
internal static class SceneFixturePaths
{
    public const string FixtureSceneId = "fixture-tileset";
    public const string ProtectedSceneId = "protected-fixture-tileset";

    public static string ResolveFixtureRoot() => SceneFixtureRoots.Resolve(FixtureSceneId);
}
