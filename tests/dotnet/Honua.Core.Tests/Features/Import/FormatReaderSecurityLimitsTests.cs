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
        var payload = new byte[CsvFormatReader.MaxRecordSizeBytes + 64];
        Array.Fill(payload, (byte)'x');
        await using var stream = new MemoryStream(payload);

        var act = async () =>
        {
            await foreach (var feature in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
            {
                _ = feature;
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CSV record exceeds maximum size limit*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void GdbTableReader_ReadBlobFieldWithIntMaxValueLength_DoesNotOverflow()
    {
        // Leading byte advances pos to 1 so pos + int.MaxValue would overflow signed Int32.
        // VarUInt encoding of 0x7FFFFFFF (int.MaxValue): 0xFF, 0xFF, 0xFF, 0xFF, 0x07.
        byte[] data = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0xAA, 0xBB];

        var result = GdbTableReader.ReadBlobField(data, pos: 1);

        result.Value.Should().BeNull();
        result.NextPosition.Should().Be(data.Length);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void GdbTableReader_ReadUtf8StringValueWithIntMaxValueLength_DoesNotOverflow()
    {
        byte[] data = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x41, 0x42];

        var result = GdbTableReader.ReadUtf8StringValue(data, pos: 1);

        result.Value.Should().BeNull();
        result.NextPosition.Should().Be(data.Length);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void GdbTableReader_ZeroLengthFieldFollowedByPopulatedField_PreservesSubsequentFieldPosition()
    {
        // Field 1: zero-length string/blob (varuint 0x00).
        // Field 2: 2-byte UTF-8 string "ok" (varuint 0x02, 'o', 'k').
        // Field 3: 2-byte blob [0xAB, 0xCD] (varuint 0x02, 0xAB, 0xCD).
        byte[] data = [0x00, 0x02, (byte)'o', (byte)'k', 0x00, 0x02, 0xAB, 0xCD];

        var emptyString = GdbTableReader.ReadUtf8StringValue(data, pos: 0);
        emptyString.Value.Should().BeNull();
        emptyString.NextPosition.Should().Be(1, "zero-length field must advance only past its length varuint");

        var nextString = GdbTableReader.ReadUtf8StringValue(data, emptyString.NextPosition);
        nextString.Value.Should().Be("ok");
        nextString.NextPosition.Should().Be(4);

        var emptyBlob = GdbTableReader.ReadBlobField(data, nextString.NextPosition);
        emptyBlob.Value.Should().BeNull();
        emptyBlob.NextPosition.Should().Be(5);

        var nextBlob = GdbTableReader.ReadBlobField(data, emptyBlob.NextPosition);
        nextBlob.Value.Should().Equal(0xAB, 0xCD);
        nextBlob.NextPosition.Should().Be(data.Length);
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
