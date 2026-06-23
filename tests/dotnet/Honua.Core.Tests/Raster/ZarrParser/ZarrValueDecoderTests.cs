// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrValueDecoderTests
{
    [Fact]
    public void TryDecodeFirst_Float32_DecodesValue()
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, 21.5f);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4), 99f);
        var result = new ZarrSubsetResult("temperature", [2], "<f4", buffer);

        ZarrValueDecoder.TryDecodeFirst(result, out var value).Should().BeTrue();
        value.Should().BeApproximately(21.5, 1e-6);
    }

    [Fact]
    public void TryDecodeFirst_Int16_DecodesValue()
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, -123);
        var result = new ZarrSubsetResult("v", [1], "<i2", buffer);

        ZarrValueDecoder.TryDecodeFirst(result, out var value).Should().BeTrue();
        value.Should().Be(-123);
    }

    [Fact]
    public void TryDecode_UnsignedByte_DecodesValue()
    {
        ZarrValueDecoder.TryDecode([200], "|u1", out var value).Should().BeTrue();
        value.Should().Be(200);
    }

    [Fact]
    public void TryDecode_EmptyBuffer_ReturnsFalse()
    {
        ZarrValueDecoder.TryDecode([], "<f8", out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_UnsupportedDtype_ReturnsFalse()
    {
        ZarrValueDecoder.TryDecode([0, 0, 0, 0], "<c8", out _).Should().BeFalse();
    }
}
