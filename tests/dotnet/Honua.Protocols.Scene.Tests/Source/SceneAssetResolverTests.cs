// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Protocols.Scene;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Unit tests for the path-traversal guard in <see cref="SceneAssetResolver"/>.
/// Integration coverage in <see cref="SceneTilesetEndpointTests"/> exercises the
/// HTTP layer; this file isolates the path-canonicalization logic so failures
/// are easy to diagnose.
/// </summary>
[Protocol(TestProtocols.Scene)]
public sealed class SceneAssetResolverTests : IAsyncLifetime, IDisposable
{
    private readonly string _root;
    private readonly string _siblingRoot;
    private readonly string _siblingSecretPath;

    public SceneAssetResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "honua-scene-resolver-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "tiles"));
        File.WriteAllText(Path.Combine(_root, "tileset.json"), "{}");
        File.WriteAllBytes(Path.Combine(_root, "tiles", "0.b3dm"), new byte[] { (byte)'b', (byte)'3', (byte)'d', (byte)'m' });

        // A sibling directory the resolver must never reach. Used by the
        // symlink-escape test to verify that links inside the asset root
        // cannot redirect file I/O to an outside target.
        _siblingRoot = Path.Combine(Path.GetDirectoryName(_root)!, "honua-scene-resolver-sibling-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_siblingRoot);
        _siblingSecretPath = Path.Combine(_siblingRoot, "secret.txt");
        File.WriteAllText(_siblingSecretPath, "should not be reachable");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_siblingRoot))
        {
            Directory.Delete(_siblingRoot, recursive: true);
        }
    }

    private SceneDataset Dataset() => new()
    {
        Id = "test",
        Name = "test",
        AssetRoot = Path.GetFullPath(_root),
        TilesetFileName = "tileset.json"
    };

    [UnitTest]
    public void TryResolve_KnownAsset_ReturnsResolvedFileAndContentType()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "tiles/0.b3dm", out var resolved, out var error);
        ok.Should().BeTrue();
        error.Should().Be(SceneAssetResolutionError.None);
        resolved.File.FullName.Should().StartWith(Path.GetFullPath(_root));
        resolved.ContentType.Should().Be("application/octet-stream");
    }

    [UnitTest]
    public void TryResolve_TilesetJson_ReturnsApplicationJson()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "tileset.json", out _, out _);
        ok.Should().BeTrue();

        SceneContentTypes.Resolve("tileset.json").Should().Be("application/json");
    }

    [UnitTest]
    public void SceneContentTypes_Resolve_AssignsCanonicalMediaTypesForKnownExtensions()
    {
        // Locks the documented MIME contract from docs/gis/scenes-3dtiles.md so
        // changes in the resolver surface here rather than in CesiumJS-side
        // resource-loader failures. Covers extensions that have no fixture
        // bytes (glTF/GLB/KTX/Basis/i3dm/pnts/cmpt/bin) since adding real
        // payloads for every extension is out of scope for this slice.
        SceneContentTypes.Resolve("model.glb").Should().Be("model/gltf-binary");
        SceneContentTypes.Resolve("model.gltf").Should().Be("model/gltf+json");
        SceneContentTypes.Resolve("tile.b3dm").Should().Be("application/octet-stream");
        SceneContentTypes.Resolve("tile.i3dm").Should().Be("application/octet-stream");
        SceneContentTypes.Resolve("cloud.pnts").Should().Be("application/octet-stream");
        SceneContentTypes.Resolve("group.cmpt").Should().Be("application/octet-stream");
        SceneContentTypes.Resolve("buffer.bin").Should().Be("application/octet-stream");
        SceneContentTypes.Resolve("tex.png").Should().Be("image/png");
        SceneContentTypes.Resolve("tex.jpg").Should().Be("image/jpeg");
        SceneContentTypes.Resolve("tex.jpeg").Should().Be("image/jpeg");
        SceneContentTypes.Resolve("tex.webp").Should().Be("image/webp");
        SceneContentTypes.Resolve("tex.ktx").Should().Be("image/ktx");
        SceneContentTypes.Resolve("tex.ktx2").Should().Be("image/ktx2");
        SceneContentTypes.Resolve("tex.basis").Should().Be("image/basis");
        SceneContentTypes.Resolve("MIXED.GLB").Should().Be("model/gltf-binary");
        SceneContentTypes.Resolve("anything.unknown").Should().Be("application/octet-stream");
        SceneContentTypes.Resolve("noextension").Should().Be("application/octet-stream");
    }

    [UnitTest]
    public void TryResolve_DotDotTraversal_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "../secret.txt", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_DotDotInsideSegments_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "tiles/../../secret.txt", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_LeadingSlash_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "/etc/passwd", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_BackslashSeparator_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "tiles\\0.b3dm", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_PercentEncodedTraversal_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "%2E%2E/secret.txt", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_DriveLetterPrefix_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "C:/Windows/win.ini", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_EmptySegment_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "tiles//0.b3dm", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_NullByte_RejectsAsInvalidPath()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "tiles/0.b3dm\0.txt", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.InvalidPath);
    }

    [UnitTest]
    public void TryResolve_MissingFile_ReturnsNotFound()
    {
        var ok = SceneAssetResolver.TryResolve(Dataset(), "tiles/missing.b3dm", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be(SceneAssetResolutionError.NotFound);
    }

    [UnitTest]
    public void TryResolve_AssetRootWithTrailingSeparator_ResolvesValidAsset()
    {
        // Regression: HasLinkBetweenFileAndRoot used a string-equality stop
        // condition that never matched when AssetRoot retained a trailing
        // directory separator (which Path.GetFullPath preserves), causing the
        // walk to overshoot the root and reject every valid file as
        // OutsideRoot.
        var datasetWithTrailingSeparator = new SceneDataset
        {
            Id = "trailing",
            Name = "trailing",
            AssetRoot = Path.GetFullPath(_root) + Path.DirectorySeparatorChar,
            TilesetFileName = "tileset.json"
        };

        var ok = SceneAssetResolver.TryResolve(datasetWithTrailingSeparator, "tiles/0.b3dm", out var resolved, out var error);
        ok.Should().BeTrue();
        error.Should().Be(SceneAssetResolutionError.None);
        resolved.File.FullName.Should().StartWith(Path.GetFullPath(_root));
    }

    [UnitTest]
    public void TryResolve_FileSymlinkEscapingAssetRoot_RejectsAsOutsideRoot()
    {
        // Lexical prefix tests already prove `..` and absolute-path inputs are
        // rejected; this case covers the subtler attack where a symlink under
        // AssetRoot points to a real file outside of it.
        var linkPath = Path.Combine(_root, "escape-link");

        try
        {
            File.CreateSymbolicLink(linkPath, _siblingSecretPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // The current OS or user cannot create symlinks (e.g., Windows
            // without Developer Mode). Skip silently — Linux CI still proves
            // the resolver path.
            return;
        }

        try
        {
            var ok = SceneAssetResolver.TryResolve(Dataset(), "escape-link", out _, out var error);
            ok.Should().BeFalse();
            error.Should().Be(SceneAssetResolutionError.OutsideRoot);
        }
        finally
        {
            File.Delete(linkPath);
        }
    }
}
