// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene;
using Honua.Core.Features.Scene.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene;

/// <summary>
/// Unit tests covering the pure shape/syntax validators used during scene
/// dataset registration.
/// </summary>
public class SceneDatasetValidatorTests
{
    [Theory]
    [InlineData("hosted-scene")]
    [InlineData("alpha")]
    [InlineData("a1")]
    [InlineData("scene-2026-q1")]
    public void TryValidateSceneId_AcceptsValidSlugs(string id)
    {
        var ok = SceneDatasetValidator.TryValidateSceneId(id, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("with space")]
    [InlineData("with_underscore")]
    public void TryValidateSceneId_RejectsInvalidSlugs(string id)
    {
        var ok = SceneDatasetValidator.TryValidateSceneId(id, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [UnitTest]
    public void TryValidateSceneId_RejectsTooLongSlug()
    {
        var id = new string('a', SceneDatasetValidator.MaxSceneIdLength + 1);

        var ok = SceneDatasetValidator.TryValidateSceneId(id, out var error);

        Assert.False(ok);
        Assert.Contains("characters or fewer", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/var/lib/honua/scenes/alpha")]
    [InlineData("/data/scene")]
    [InlineData("./relative/path/to/scene")]
    public void TryValidateAssetRoot_AcceptsLocalPaths(string assetRoot)
    {
        var ok = SceneDatasetValidator.TryValidateAssetRoot(assetRoot, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("https://cdn.example.com/scene")]
    [InlineData("http://localhost/scene")]
    [InlineData("file:///var/lib/scene")]
    [InlineData("/var/lib/../etc/passwd")]
    [InlineData("c:\\scenes\\alpha")]
    [InlineData("/var/lib/scene; rm -rf /")]
    [InlineData("/var/lib/scene | cat")]
    [InlineData("/var/lib/scene&")]
    [InlineData("/var/lib/scene$VAR")]
    [InlineData("/var/lib/scene*")]
    public void TryValidateAssetRoot_RejectsUnsafeRoots(string assetRoot)
    {
        var ok = SceneDatasetValidator.TryValidateAssetRoot(assetRoot, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("EPSG:4326")]
    [InlineData("EPSG:4979")]
    [InlineData("OGC:1")]
    public void TryValidateCrs_AcceptsAuthorityTokens(string crs)
    {
        var ok = SceneDatasetValidator.TryValidateCrs(crs, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [UnitTest]
    public void TryValidateCrs_AcceptsNull()
    {
        var ok = SceneDatasetValidator.TryValidateCrs(null, out var error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("epsg:4326")]
    [InlineData("EPSG-4326")]
    [InlineData("4326")]
    [InlineData("urn:ogc:def:crs:EPSG::4326")]
    [InlineData("")]
    public void TryValidateCrs_RejectsInvalidFormats(string crs)
    {
        var ok = SceneDatasetValidator.TryValidateCrs(crs, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [UnitTest]
    public void TryValidateCrs_RejectsTooLongToken()
    {
        // The regex on its own accepts any all-digits code, so an overlong
        // token would still pass shape validation and only fail at INSERT
        // time against the VARCHAR(32) crs column. The length guard rejects
        // it up front with an admin problem-details message instead.
        const string prefix = "EPSG:";
        var crs = prefix + new string('1', SceneDatasetValidator.MaxCrsLength - prefix.Length + 1);

        var ok = SceneDatasetValidator.TryValidateCrs(crs, out var error);

        Assert.True(crs.Length > SceneDatasetValidator.MaxCrsLength);
        Assert.False(ok);
        Assert.Contains("characters or fewer", error, StringComparison.Ordinal);
    }

    [UnitTest]
    public void TryValidateCachePolicy_AcceptsBoundary()
    {
        Assert.True(SceneDatasetValidator.TryValidateCachePolicy(new SceneCachePolicy(0, false), out _));
        Assert.True(SceneDatasetValidator.TryValidateCachePolicy(new SceneCachePolicy(86_400, true), out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void TryValidateCachePolicy_RejectsOutOfRange(int seconds)
    {
        var ok = SceneDatasetValidator.TryValidateCachePolicy(new SceneCachePolicy(seconds, false), out var error);

        Assert.False(ok);
        Assert.Contains("Cache max-age", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("enterprise")]
    [InlineData("preview-2")]
    public void TryValidateEditionGate_AcceptsLowerKebab(string slug)
    {
        Assert.True(SceneDatasetValidator.TryValidateEditionGate(slug, out _));
    }

    [UnitTest]
    public void TryValidateEditionGate_AcceptsNull()
    {
        Assert.True(SceneDatasetValidator.TryValidateEditionGate(null, out _));
    }

    [Theory]
    [InlineData("Pro")]
    [InlineData("UPPER")]
    [InlineData("with space")]
    [InlineData("")]
    public void TryValidateEditionGate_RejectsInvalid(string? slug)
    {
        Assert.False(SceneDatasetValidator.TryValidateEditionGate(slug, out _));
    }

    [UnitTest]
    public void TryValidateExtent_AcceptsNull()
    {
        Assert.True(SceneDatasetValidator.TryValidateExtent(null, out _));
    }

    [UnitTest]
    public void TryValidateExtent_AcceptsValid()
    {
        var extent = new SceneExtent(-180, -90, 180, 90);

        Assert.True(SceneDatasetValidator.TryValidateExtent(extent, out _));
    }

    [Theory]
    [InlineData(-181d, 0d, 1d, 1d)]
    [InlineData(0d, -91d, 1d, 1d)]
    [InlineData(2d, 0d, 1d, 1d)]
    [InlineData(0d, 2d, 1d, 1d)]
    public void TryValidateExtent_RejectsInvalid(double xmin, double ymin, double xmax, double ymax)
    {
        var ok = SceneDatasetValidator.TryValidateExtent(new SceneExtent(xmin, ymin, xmax, ymax), out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [UnitTest]
    public void TryValidateAccessFlags_RejectsConflict()
    {
        var ok = SceneDatasetValidator.TryValidateAccessFlags(isPublic: true, requiresAuth: true, out var error);

        Assert.False(ok);
        Assert.Contains("public", error, StringComparison.Ordinal);
    }

    [UnitTest]
    public void TryValidateAccessFlags_RejectsBothFalse()
    {
        // Both-false is a contradiction: the registry projects any non-public
        // record to a protected access policy, so the admin-visible
        // requiresAuth=false would lie about how the scene actually serves.
        var ok = SceneDatasetValidator.TryValidateAccessFlags(isPublic: false, requiresAuth: false, out var error);

        Assert.False(ok);
        Assert.Contains("exactly one", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TryValidateAccessFlags_AcceptsConsistentCombinations(bool isPublic, bool requiresAuth)
    {
        Assert.True(SceneDatasetValidator.TryValidateAccessFlags(isPublic, requiresAuth, out _));
    }

    [Theory]
    [InlineData("tileset.json")]
    [InlineData("scene.json")]
    [InlineData("root-tileset.json")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidateTilesetFileName_AcceptsSafeRelativeNames(string? tilesetFileName)
    {
        Assert.True(SceneDatasetValidator.TryValidateTilesetFileName(tilesetFileName, out _));
    }

    [Theory]
    [InlineData("nested/tileset.json")]
    [InlineData("nested\\tileset.json")]
    [InlineData("../tileset.json")]
    [InlineData("..tileset.json")]
    [InlineData("tileset|.json")]
    [InlineData("tileset.json;rm")]
    [InlineData("tileset$x.json")]
    public void TryValidateTilesetFileName_RejectsUnsafeNames(string tilesetFileName)
    {
        var ok = SceneDatasetValidator.TryValidateTilesetFileName(tilesetFileName, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [UnitTest]
    public void TryValidateTilesetFileName_RejectsTooLong()
    {
        var tilesetFileName = new string('a', SceneDatasetValidator.MaxTilesetFileNameLength + 1);

        var ok = SceneDatasetValidator.TryValidateTilesetFileName(tilesetFileName, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
