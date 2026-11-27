// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.Generation;
using Honua.Infrastructure.Scene;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class SceneTilesetArchiveImporterTests : IDisposable
{
    private readonly string _outputRoot;
    private readonly StubRegistrationService _registration;
    private readonly SceneTilesetArchiveImporter _importer;

    public SceneTilesetArchiveImporterTests()
    {
        _outputRoot = Path.Join(Path.GetTempPath(), $"honua-archive-{Guid.NewGuid():N}");
        _registration = new StubRegistrationService();
        var options = Options.Create(new SceneGenerationServerOptions
        {
            OutputRoot = _outputRoot,
            GeneratorTag = "honua-test-generator/1.0",
        });

        _importer = new SceneTilesetArchiveImporter(
            new TestHostEnvironment(),
            options,
            NullLogger<SceneTilesetArchiveImporter>.Instance,
            _registration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [UnitTest]
    public async Task ImportAsync_ValidZipArchive_PromotesAndRegistersScene()
    {
        var glb = BuildSampleGlb();
        var tilesetJson = BuildSampleTilesetJson("tiles/0.glb");
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(tilesetJson)),
            ("tiles/0.glb", glb));

        var outcome = await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "pre-tiled-zip", DisplayName: "Pre-Tiled Zip"),
            CancellationToken.None);

        outcome.SceneId.Should().Be("pre-tiled-zip");
        outcome.TilesetFileName.Should().Be("tileset.json");
        outcome.TilesetVersion.Should().Be("1.1");
        outcome.FileCount.Should().Be(2);
        outcome.TotalBytes.Should().Be(Encoding.UTF8.GetByteCount(tilesetJson) + glb.Length);
        outcome.Extent.Should().NotBeNull();
        outcome.Extent!.XMin.Should().BeApproximately(-157.86, 1e-3);
        outcome.Extent.YMin.Should().BeApproximately(21.30, 1e-3);

        File.Exists(Path.Join(outcome.AssetRoot, "tileset.json")).Should().BeTrue();
        File.Exists(Path.Join(outcome.AssetRoot, "tiles", "0.glb")).Should().BeTrue();
        Directory.GetDirectories(_outputRoot, ".staging-*").Should().BeEmpty();

        _registration.Records.Should().ContainSingle();
        _registration.Records[0].Id.Should().Be("pre-tiled-zip");
        _registration.Records[0].DatasetType.Should().Be(SceneDatasetType.HostedTiles);
    }

    [UnitTest]
    public async Task ImportAsync_ThreeTzArchiveWithIndexAndWrapperFolder_UnwrapsAndIgnores3TzIndex()
    {
        var glb = BuildSampleGlb();
        var tilesetJson = BuildSampleTilesetJson("0.glb");
        var zip = CreateZip(
            ("@3dtilesIndex1@", new byte[] { 1, 2, 3, 4 }),
            ("nested-root/tileset.json", Encoding.UTF8.GetBytes(tilesetJson)),
            ("nested-root/0.glb", glb));

        var outcome = await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "three-tz-scene"),
            CancellationToken.None);

        outcome.FileCount.Should().Be(2);
        File.Exists(Path.Join(outcome.AssetRoot, "tileset.json")).Should().BeTrue();
        File.Exists(Path.Join(outcome.AssetRoot, "0.glb")).Should().BeTrue();
        File.Exists(Path.Join(outcome.AssetRoot, "@3dtilesIndex1@")).Should().BeFalse();
    }

    [UnitTest]
    public async Task ImportAsync_ZipSlipTraversal_RejectsAndCleansStaging()
    {
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("0.glb"))),
            ("../escape.glb", BuildSampleGlb()));

        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "zip-slip"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
        _registration.Records.Should().BeEmpty();
        if (Directory.Exists(_outputRoot))
        {
            Directory.GetDirectories(_outputRoot, ".staging-*").Should().BeEmpty();
        }
    }

    [UnitTest]
    public async Task ImportAsync_DisallowedExtension_RejectsArchive()
    {
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("0.glb"))),
            ("0.glb", BuildSampleGlb()),
            ("payload.exe", new byte[] { 0x4D, 0x5A }));

        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "bad-ext"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("disallowed extension");
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ImportAsync_ExceedsEntryCountCap_RejectsArchive()
    {
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("0.glb"))),
            ("0.glb", BuildSampleGlb()),
            ("extra.bin", new byte[] { 1 }));

        var limits = new SceneTilesetArchiveImportOptions(MaxEntryCount: 2);
        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "too-many", Limits: limits),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("entry cap");
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ImportAsync_DirectoryEntriesExceedingCap_RejectsArchive()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 5; i++)
            {
                archive.CreateEntry($"dir{i}/");
            }

            var entry = archive.CreateEntry("tileset.json", CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(BuildSampleTilesetJson("0.glb"));
            stream.Write(bytes, 0, bytes.Length);
        }

        var limits = new SceneTilesetArchiveImportOptions(MaxEntryCount: 4);
        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(memory.ToArray(), SceneId: "too-many-dirs", Limits: limits),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("entry cap");
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ImportAsync_CompressionRatioBomb_RejectsArchive()
    {
        var bombPayload = new byte[2 * 1024 * 1024];
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("bomb.bin"))),
            ("bomb.bin", bombPayload));

        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "ratio-bomb"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("compression ratio");
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task CopyBoundedAsync_ActualBytesExceedCompressionRatio_ThrowsValidationException()
    {
        Directory.CreateDirectory(_outputRoot);
        var destination = Path.Join(_outputRoot, "bomb.bin");
        await using var stream = new MemoryStream(new byte[32 * 1024]);
        var copyContext = new ArchiveCopyContext
        {
            EntryStream = stream,
            RelativePath = "bomb.bin",
            CompressedLength = 100,
            DestinationPath = destination,
            TotalBytes = 0,
            Limits = SceneTilesetArchiveImportOptions.Default,
            Buffer = new byte[8192]
        };

        var act = () => SceneTilesetArchiveEntryCopier.CopyBoundedAsync(copyContext, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("compression ratio");
    }

    [UnitTest]
    public async Task ImportAsync_ExceedsUncompressedByteBudget_RejectsZipBomb()
    {
        var compressiblePayload = new byte[64 * 1024];
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("big.bin"))),
            ("big.bin", compressiblePayload));

        var limits = new SceneTilesetArchiveImportOptions(
            MaxUncompressedBytes: 4096,
            MaxSingleEntryBytes: 4096);
        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "zip-bomb", Limits: limits),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.OptionsInvalid);
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ImportAsync_MissingReferencedTileContent_RejectsArchive()
    {
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("tiles/missing.glb"))));

        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "missing-tile"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain("missing content file");
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ImportAsync_DuplicateSceneId_ThrowsRegistrationConflict()
    {
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("0.glb"))),
            ("0.glb", BuildSampleGlb()));

        await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "dup-archive"),
            CancellationToken.None);

        var act = async () => await _importer.ImportAsync(
            new SceneTilesetArchiveImportRequest(zip, SceneId: "dup-archive"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().StartWith(SceneGenerationErrorCodes.SceneRegistrationConflict);
    }

    [UnitTest]
    public async Task ImportAsync_ExternalTileset_ResolvesContentRelativeToItsDocument()
    {
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("nested/child.json"))),
            ("nested/child.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("../shared/tile.glb"))),
            ("shared/tile.glb", BuildSampleGlb()));

        await _importer.ImportAsync(new(zip, SceneId: "nested-valid"), CancellationToken.None);

        _registration.Records.Should().ContainSingle();
    }

    [UnitTheory]
    [InlineData("missing.glb", "missing content file")]
    [InlineData("TILE.glb", "missing content file")]
    [InlineData("../tileset.json", "cycle")]
    [InlineData("../../tile.glb", "safe archive-relative path")]
    public async Task ImportAsync_InvalidExternalTileset_RejectsArchive(string contentUri, string error)
    {
        var zip = CreateZip(
            ("tileset.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson("nested/child.json"))),
            ("nested/child.json", Encoding.UTF8.GetBytes(BuildSampleTilesetJson(contentUri))),
            ("nested/tile.glb", BuildSampleGlb()));

        var act = () => _importer.ImportAsync(new(zip, SceneId: "nested-invalid"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Message.Should().Contain(error);
        _registration.Records.Should().BeEmpty();
    }

    [UnitTest]
    public async Task UploadTreeAsync_ConcurrentPublicationsForSameScene_UseSeparateAssetKeys()
    {
        Directory.CreateDirectory(_outputRoot);
        await File.WriteAllTextAsync(Path.Join(_outputRoot, "tileset.json"), "{}");
        var storage = new InMemoryCloudFileStorage();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var prefixes = await Task.WhenAll(
            SceneAssetStorageUploader.UploadTreeAsync(storage, _outputRoot, "publish-race", CancellationToken.None, first),
            SceneAssetStorageUploader.UploadTreeAsync(storage, _outputRoot, "publish-race", CancellationToken.None, second));

        prefixes.Should().Equal($"scenes/publish-race/{first:N}", $"scenes/publish-race/{second:N}");
        storage.Keys.Should().Contain($"scenes/publish-race/{first:N}/tileset.json");
        storage.Keys.Should().Contain($"scenes/publish-race/{second:N}/tileset.json");
    }

    private static byte[] BuildSampleGlb()
        => ObjToGlbConverter.Convert(Encoding.UTF8.GetBytes("v 0 0 0\nv 10 0 0\nv 0 10 0\nf 1 2 3\n")).GlbBytes;

    private static string BuildSampleTilesetJson(string contentUri)
    {
        const double degToRad = Math.PI / 180.0;
        var west = -157.86 * degToRad;
        var south = 21.30 * degToRad;
        var east = -157.85 * degToRad;
        var north = 21.31 * degToRad;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $$"""
            {
              "asset": { "version": "1.1" },
              "geometricError": 100.0,
              "root": {
                "boundingVolume": { "region": [{{west}}, {{south}}, {{east}}, {{north}}, 0.0, 25.0] },
                "geometricError": 100.0,
                "refine": "ADD",
                "children": [
                  {
                    "boundingVolume": { "region": [{{west}}, {{south}}, {{east}}, {{north}}, 0.0, 25.0] },
                    "geometricError": 0.0,
                    "content": { "uri": "{{contentUri}}" }
                  }
                ]
              }
            }
            """);
    }

    private static byte[] CreateZip(params (string EntryName, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return memory.ToArray();
    }
}
