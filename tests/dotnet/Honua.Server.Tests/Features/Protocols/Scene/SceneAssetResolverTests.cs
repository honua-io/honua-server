// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Scene.Domain;
using Honua.Server.Features.Protocols.Scene;
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

    public SceneAssetResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "honua-scene-resolver-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "tiles"));
        File.WriteAllText(Path.Combine(_root, "tileset.json"), "{}");
        File.WriteAllBytes(Path.Combine(_root, "tiles", "0.b3dm"), new byte[] { (byte)'b', (byte)'3', (byte)'d', (byte)'m' });

        // A sibling directory the resolver must never reach.
        var siblingRoot = Path.Combine(Path.GetDirectoryName(_root)!, "honua-scene-resolver-sibling-" + Path.GetRandomFileName());
        Directory.CreateDirectory(siblingRoot);
        File.WriteAllText(Path.Combine(siblingRoot, "secret.txt"), "should not be reachable");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
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
}
