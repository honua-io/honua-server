// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.Core.Features.Import.Domain;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.Import;

public sealed class FormatReaderSecurityLimitsTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public async Task CsvFormatReader_SingleLongLineWithoutNewlinesExceedingCap_ThrowsInvalidOperationException()
    {
        var singleLongLine = "id,note," + new string('x', 4096);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(singleLongLine));

        var act = async () =>
        {
            await foreach (var feature in CsvFormatReader.ReadStreamingAsync(
                stream,
                delimiterOverride: ',',
                diagnostics: null,
                options: null,
                maxRecordSizeBytes: 512,
                CancellationToken.None))
            {
                _ = feature;
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CSV record exceeds maximum size limit*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void GdbTableReader_ReadBlobValueWithIntMaxValueLength_DoesNotOverflow()
    {
        // Leading byte advances pos to 1 so pos + int.MaxValue would overflow signed Int32.
        // VarUInt encoding of 0x7FFFFFFF (int.MaxValue): 0xFF, 0xFF, 0xFF, 0xFF, 0x07.
        byte[] data = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0xAA, 0xBB];
        var pos = 1;

        var blob = GdbTableReader.ReadBlobValue(data, ref pos);

        blob.Should().BeNull();
        pos.Should().Be(data.Length);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void GdbTableReader_ReadStringValueWithIntMaxValueLength_DoesNotOverflow()
    {
        byte[] data = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x41, 0x42];
        var pos = 1;

        var text = GdbTableReader.ReadStringValue(data, ref pos, useUtf8Strings: true);

        text.Should().BeNull();
        pos.Should().Be(data.Length);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void GdbGeometryReader_NumPointsExceedingMaxVertices_ReturnsNullWithoutLargeAllocation()
    {
        var geomDef = new GdbGeometryDef { XYScale = 1, ZScale = 1, MScale = 1 };
        var reader = new GdbGeometryReader(new GeometryFactory(), geomDef, new ImportLimits { MaxVertices = 16, MaxRings = 4 });
        // ShptArea (5), numPoints = 32, numParts = 1, padded with enough bytes so blob.Length >= 32.
        var blob = new byte[64];
        blob[0] = 5;
        blob[1] = 32;
        blob[2] = 1;

        var geometry = reader.ReadGeometry(blob);

        geometry.Should().BeNull();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void GdbGeometryReader_NumPartsExceedingMaxRings_ReturnsNullWithoutLargeAllocation()
    {
        var geomDef = new GdbGeometryDef { XYScale = 1, ZScale = 1, MScale = 1 };
        var reader = new GdbGeometryReader(new GeometryFactory(), geomDef, new ImportLimits { MaxVertices = 64, MaxRings = 2 });
        // ShptArea (5), numPoints = 10, numParts = 5 (> MaxRings = 2).
        var blob = new byte[64];
        blob[0] = 5;
        blob[1] = 10;
        blob[2] = 5;

        var geometry = reader.ReadGeometry(blob);

        geometry.Should().BeNull();
    }
}
