// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Protocols.Scene;

internal static class SceneFixturePaths
{
    public const string FixtureSceneId = "fixture-tileset";
    public const string ProtectedSceneId = "protected-fixture-tileset";

    public static string ResolveFixtureRoot() => SceneFixtureRoots.Resolve(FixtureSceneId);
}
