// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class ShapefileHeaderTests
{
    [UnitTheory]
    [InlineData(0, false, 100)]
    [InlineData(1, false, 128)]
    [InlineData(2, false, 156)]
    [InlineData(1, true, 112)]
    [InlineData(2, true, 124)]
    public async Task WriteAsync_PointRows_DeclaresActualFileLengths(
        int rowCount, bool nullGeometry, int shapeLength)
    {
        await using var output = new MemoryStream();
        var result = await ShapefileExportWriter.WriteAsync(
            output, Rows(rowCount, nullGeometry),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point, null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(rowCount, result.WrittenCount);
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        var shape = await ReadEntryAsync(archive, "export.shp");
        var index = await ReadEntryAsync(archive, "export.shx");
        var dbf = await ReadEntryAsync(archive, "export.dbf");

        AssertHeader(shape, shapeLength);
        AssertHeader(index, 100 + 8 * rowCount);
        Assert.Equal(rowCount, BinaryPrimitives.ReadInt32LittleEndian(dbf.AsSpan(4, 4)));
        if (rowCount > 0)
        {
            Assert.Equal(50, BinaryPrimitives.ReadInt32BigEndian(index.AsSpan(100, 4)));
            Assert.Equal(nullGeometry ? 2 : 10,
                BinaryPrimitives.ReadInt32BigEndian(index.AsSpan(104, 4)));
            Assert.Equal(nullGeometry ? 0 : 1,
                BinaryPrimitives.ReadInt32LittleEndian(shape.AsSpan(108, 4)));
        }
    }

    [UnitTest]
    public async Task WriteAsync_CancelledEmptyExport_DoesNotPublishArchive()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var output = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShapefileExportWriter.WriteAsync(
            output, Rows(0, false), [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point, null, NullLogger.Instance, cancellation.Token));

        Assert.Equal(0, output.Length);
    }

    private static void AssertHeader(byte[] bytes, int expectedLength)
    {
        Assert.Equal(expectedLength, bytes.Length);
        Assert.Equal(9994, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(expectedLength / 2, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24, 4)));
        Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32, 4)));
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        await using var stream = entry.Open();
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private static async IAsyncEnumerable<Feature> Rows(int count, bool nullGeometry)
    {
        await Task.CompletedTask;
        var geometry = nullGeometry ? null : new WKBWriter().Write(new Point(10, 20));
        for (var i = 0; i < count; i++)
        {
            yield return Feature.Create(i + 1, geometry,
                ImmutableDictionary<string, object?>.Empty.Add("name", $"row-{i}"));
        }
    }
}
