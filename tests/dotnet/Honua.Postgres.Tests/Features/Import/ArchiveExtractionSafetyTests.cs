// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class ArchiveExtractionSafetyTests
{
    [Fact]
    public async Task PreviewFileAsync_KmzWithExcessiveCompressionRatio_ThrowsInvalidDataException()
    {
        var limits = new ImportLimits
        {
            MaxArchiveCompressionRatio = 10,
            MaxArchiveEntryBytes = 10 * 1024 * 1024,
            MaxArchiveExtractedBytes = 20 * 1024 * 1024
        };

        await using var stream = CreateZipArchive(
            ("doc.kml", Encoding.UTF8.GetBytes(new string('A', 2 * 1024 * 1024))));
        var service = CreateService(limits);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.PreviewFileAsync(stream, "malicious.kmz"));

        exception.Message.Should().Contain("compression ratio");
    }

    [Fact]
    public async Task PreviewFileAsync_ShapefileZipExceedingEntryLimit_ThrowsInvalidDataException()
    {
        var limits = new ImportLimits
        {
            MaxArchiveEntryBytes = 1024,
            MaxArchiveExtractedBytes = 10 * 1024,
            MaxArchiveCompressionRatio = 10_000
        };

        await using var stream = CreateZipArchive(
            ("layer.shp", CreatePatternBytes(2048)),
            ("layer.dbf", CreatePatternBytes(128)));
        var service = CreateService(limits);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.PreviewFileAsync(stream, "malicious.zip"));

        exception.Message.Should().Contain("maximum uncompressed size");
    }

    [Fact]
    public async Task PreviewFileAsync_ShapefileZipExceedingTotalExtractionLimit_ThrowsInvalidDataException()
    {
        var limits = new ImportLimits
        {
            MaxArchiveEntryBytes = 2000,
            MaxArchiveExtractedBytes = 2500,
            MaxArchiveCompressionRatio = 10_000
        };

        await using var stream = CreateZipArchive(
            ("layer.shp", CreatePatternBytes(1600)),
            ("layer.dbf", CreatePatternBytes(1600)));
        var service = CreateService(limits);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.PreviewFileAsync(stream, "malicious.zip"));

        exception.Message.Should().Contain("maximum total uncompressed size");
    }

    [Fact]
    public async Task PreviewFileAsync_ShapefileZip_DoesNotPairComponentsAcrossDirectories()
    {
        await using var stream = CreateZipArchive(
            ("alpha/roads.shp", CreatePatternBytes(128)),
            ("beta/roads.dbf", CreatePatternBytes(128)));
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.PreviewFileAsync(stream, "mixed.zip"));

        exception.Message.Should().Contain("required .shp and .dbf");
    }

    private static IFileImportService CreateService(ImportLimits limits) =>
        PreviewImportServiceFactory.Create(limits);

    private static IFileImportService CreateService() =>
        PreviewImportServiceFactory.Create();

    private static MemoryStream CreateZipArchive(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static byte[] CreatePatternBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

}
