// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.FeatureServer.Services;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

public sealed class ProtobufWriterTests
{
    // ── Varint encoding ────────────────────────────────────────

    [Theory]
    [InlineData(0u, new byte[] { 0x00 })]
    [InlineData(1u, new byte[] { 0x01 })]
    [InlineData(127u, new byte[] { 0x7F })]
    [InlineData(128u, new byte[] { 0x80, 0x01 })]
    [InlineData(300u, new byte[] { 0xAC, 0x02 })]
    [InlineData(16384u, new byte[] { 0x80, 0x80, 0x01 })]
    public void WriteRawVarint_UInt32_EncodesCorrectly(uint value, byte[] expected)
    {
        var writer = new ProtobufWriter(16);
        writer.WriteRawVarint(value);
        var result = writer.ToArrayAndDispose();

        result.Should().Equal(expected);
    }

    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(1UL, new byte[] { 0x01 })]
    [InlineData(300UL, new byte[] { 0xAC, 0x02 })]
    public void WriteRawVarint_UInt64_EncodesCorrectly(ulong value, byte[] expected)
    {
        var writer = new ProtobufWriter(16);
        writer.WriteRawVarint(value);
        var result = writer.ToArrayAndDispose();

        result.Should().Equal(expected);
    }

    // ── Tag encoding ───────────────────────────────────────────

    [Fact]
    public void WriteTag_Field1Varint_EncodesAs0x08()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteTag(1, 0); // field 1, wire type 0 (varint)
        var result = writer.ToArrayAndDispose();

        result.Should().Equal(new byte[] { 0x08 }); // (1 << 3) | 0 = 8
    }

    [Fact]
    public void WriteTag_Field1LengthDelimited_EncodesAs0x0A()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteTag(1, 2); // field 1, wire type 2 (length-delimited)
        var result = writer.ToArrayAndDispose();

        result.Should().Equal(new byte[] { 0x0A }); // (1 << 3) | 2 = 10
    }

    [Fact]
    public void WriteTag_Field2Varint_EncodesAs0x10()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteTag(2, 0);
        var result = writer.ToArrayAndDispose();

        result.Should().Equal(new byte[] { 0x10 }); // (2 << 3) | 0 = 16
    }

    // ── Scalar fields ──────────────────────────────────────────

    [Fact]
    public void WriteUInt32_Zero_SkippedAsProto3Default()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteUInt32(1, 0);
        var result = writer.ToArrayAndDispose();

        result.Should().BeEmpty("proto3 default values are not serialized");
    }

    [Fact]
    public void WriteUInt32_NonZero_WritesTagAndValue()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteUInt32(1, 150);
        var result = writer.ToArrayAndDispose();

        // tag: 0x08 (field 1, varint), value: 150 = 0x96 0x01
        result.Should().Equal(new byte[] { 0x08, 0x96, 0x01 });
    }

    [Fact]
    public void WriteBool_False_SkippedAsProto3Default()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteBool(1, false);
        var result = writer.ToArrayAndDispose();

        result.Should().BeEmpty();
    }

    [Fact]
    public void WriteBool_True_WritesTagAndOne()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteBool(1, true);
        var result = writer.ToArrayAndDispose();

        result.Should().Equal(new byte[] { 0x08, 0x01 });
    }

    [Fact]
    public void WriteString_Empty_Skipped()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteString(1, "");
        var result = writer.ToArrayAndDispose();

        result.Should().BeEmpty();
    }

    [Fact]
    public void WriteString_Null_Skipped()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteString(1, null);
        var result = writer.ToArrayAndDispose();

        result.Should().BeEmpty();
    }

    [Fact]
    public void WriteString_Value_WritesTagLengthAndUtf8()
    {
        var writer = new ProtobufWriter(32);
        writer.WriteString(1, "abc");
        var result = writer.ToArrayAndDispose();

        // tag 0x0A (field 1, wire type 2), length 3, "abc"
        result.Should().Equal(new byte[] { 0x0A, 0x03, 0x61, 0x62, 0x63 });
    }

    [Fact]
    public void WriteSInt32_Zero_Skipped()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteSInt32(1, 0);
        var result = writer.ToArrayAndDispose();

        result.Should().BeEmpty();
    }

    [Fact]
    public void WriteSInt32_Positive_ZigZagEncoded()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteSInt32(1, 1);
        var result = writer.ToArrayAndDispose();

        // zigzag(1) = 2, so tag 0x08 + value 0x02
        result.Should().Equal(new byte[] { 0x08, 0x02 });
    }

    [Fact]
    public void WriteSInt32_Negative_ZigZagEncoded()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteSInt32(1, -1);
        var result = writer.ToArrayAndDispose();

        // zigzag(-1) = 1, so tag 0x08 + value 0x01
        result.Should().Equal(new byte[] { 0x08, 0x01 });
    }

    [Fact]
    public void WriteDouble_Zero_Skipped()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteDouble(1, 0.0);
        var result = writer.ToArrayAndDispose();

        result.Should().BeEmpty();
    }

    [Fact]
    public void WriteDouble_NonZero_WritesTagAnd8Bytes()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteDouble(1, 1.5);
        var result = writer.ToArrayAndDispose();

        // tag 0x09 (field 1, wire type 1 = 64-bit), then 8 bytes of double
        result[0].Should().Be(0x09);
        result.Length.Should().Be(9);
        BitConverter.ToDouble(result, 1).Should().Be(1.5);
    }

    [Fact]
    public void WriteFloat_NonZero_WritesTagAnd4Bytes()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteFloat(1, 2.5f);
        var result = writer.ToArrayAndDispose();

        // tag 0x0D (field 1, wire type 5 = 32-bit), then 4 bytes of float
        result[0].Should().Be(0x0D);
        result.Length.Should().Be(5);
        BitConverter.ToSingle(result, 1).Should().Be(2.5f);
    }

    // ── Sub-message ────────────────────────────────────────────

    [Fact]
    public void WriteMessage_EmptySub_Skipped()
    {
        var outer = new ProtobufWriter(16);
        var inner = new ProtobufWriter(16);
        outer.WriteMessage(1, ref inner);
        var result = outer.ToArrayAndDispose();
        inner.Dispose();

        result.Should().BeEmpty("empty sub-messages are not written");
    }

    [Fact]
    public void WriteMessage_NonEmptySub_WritesLengthDelimitedContent()
    {
        var outer = new ProtobufWriter(32);
        var inner = new ProtobufWriter(16);
        inner.WriteBool(1, true); // 2 bytes: tag + 1

        outer.WriteMessage(1, ref inner);
        var result = outer.ToArrayAndDispose();
        inner.Dispose();

        // outer tag: 0x0A (field 1, wire type 2), length: 2, then inner bytes
        result.Should().Equal(new byte[] { 0x0A, 0x02, 0x08, 0x01 });
    }

    // ── Packed repeated fields ─────────────────────────────────

    [Fact]
    public void WritePackedUInt32_Empty_Skipped()
    {
        var writer = new ProtobufWriter(16);
        writer.WritePackedUInt32(1, ReadOnlySpan<uint>.Empty);
        var result = writer.ToArrayAndDispose();

        result.Should().BeEmpty();
    }

    [Fact]
    public void WritePackedUInt32_SingleValue_WritesCorrectly()
    {
        var writer = new ProtobufWriter(32);
        uint[] values = [5];
        writer.WritePackedUInt32(1, values);
        var result = writer.ToArrayAndDispose();

        // tag 0x0A (field 1, wire type 2), length 1, value 5
        result.Should().Equal(new byte[] { 0x0A, 0x01, 0x05 });
    }

    // ── Buffer growth ──────────────────────────────────────────

    [Fact]
    public void Writer_ExceedingInitialCapacity_GrowsAutomatically()
    {
        var writer = new ProtobufWriter(4); // tiny initial buffer
        writer.WriteString(1, "This string is much longer than 4 bytes and should trigger buffer growth");
        var result = writer.ToArrayAndDispose();

        result.Length.Should().BeGreaterThan(4);
        // Verify the string content is intact
        var decoded = System.Text.Encoding.UTF8.GetString(result, 2, result[1]);
        decoded.Should().Be("This string is much longer than 4 bytes and should trigger buffer growth");
    }

    // ── Dispose ────────────────────────────────────────────────

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteBool(1, true);
        writer.Dispose();
        writer.Dispose(); // should not throw
    }

    [Fact]
    public void ToArrayAndDispose_ReturnsCorrectBytes()
    {
        var writer = new ProtobufWriter(16);
        writer.WriteBool(1, true);
        var result = writer.ToArrayAndDispose();

        result.Should().Equal(new byte[] { 0x08, 0x01 });
    }
}
