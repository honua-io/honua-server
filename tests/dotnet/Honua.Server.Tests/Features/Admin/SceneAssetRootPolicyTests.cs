// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class SceneAssetRootPolicyTests
{
    private const string ContentRoot = "/app";
    private static readonly string[] Directories = ["/var/lib/honua/scenes", "scenes-generated"];

    [UnitTheory]
    [InlineData("/var/lib/honua/scenes/downtown")]
    [InlineData("scenes-generated/city")]
    [InlineData("/app/scenes-generated/city")]
    public void IsInside_RootUnderASceneDirectory_IsAllowed(string assetRoot)
        => Assert.True(SceneAssetRootPolicy.IsInside(ContentRoot, assetRoot, Directories));

    [UnitTheory]
    [InlineData("/proc/self")]
    [InlineData("/var/lib/honua/scenes")]
    [InlineData("/var/lib/honua/scenes/")]
    [InlineData("scenes-generated")]
    [InlineData("/app")]
    [InlineData(".")]
    [InlineData("/var/lib/honua/scenes-elsewhere")]
    [InlineData("/var/lib/honua")]
    public void IsInside_RootOutsideTheSceneDirectories_IsRefused(string assetRoot)
        => Assert.False(SceneAssetRootPolicy.IsInside(ContentRoot, assetRoot, Directories));
}
