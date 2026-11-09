// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

internal ref struct ProtobufWriter
{
    private byte[] _buffer;
    private int _position;

    public ProtobufWriter(int initialCapacity = 4096)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _position = 0;
    }

    public readonly int Position => _position;

    public byte[] ToArrayAndDispose()
    {
        var result = _buffer.AsSpan(0, _position).ToArray();
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _position = 0;
        return result;
    }

    public readonly ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _position);

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
    }

    // ── Tag writing ───────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTag(int fieldNumber, int wireType)
    {
        WriteRawVarint((uint)((fieldNumber << 3) | wireType));
    }

    // ── Varint encoding ───────────────────────────────────────

    public void WriteRawVarint(uint value)
    {
        EnsureCapacity(5);
        while (value > 0x7F)
        {
            _buffer[_position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        _buffer[_position++] = (byte)value;
    }

    public void WriteRawVarint(ulong value)
    {
        EnsureCapacity(10);
        while (value > 0x7F)
        {
            _buffer[_position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        _buffer[_position++] = (byte)value;
    }

    // ── Scalar field writers ──────────────────────────────────

    public void WriteUInt32(int fieldNumber, uint value)
    {
        if (value == 0)
            return; // proto3 default
        WriteTag(fieldNumber, 0);
        WriteRawVarint(value);
    }

    public void WriteInt64(int fieldNumber, long value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint((ulong)value);
    }

    public void WriteUInt64(int fieldNumber, ulong value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint(value);
    }

    public void WriteSInt32(int fieldNumber, int value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint(ZigZagEncode32(value));
    }

    public void WriteSInt64(int fieldNumber, long value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint(ZigZagEncode64(value));
    }

    public void WriteBool(int fieldNumber, bool value)
    {
        if (!value)
            return; // proto3 default
        WriteTag(fieldNumber, 0);
        EnsureCapacity(1);
        _buffer[_position++] = 1;
    }

    public void WriteEnum(int fieldNumber, int value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint((uint)value);
    }

    public void WriteDouble(int fieldNumber, double value)
    {
        // Intentional exact comparison: proto3 wire format omits scalar fields equal to the
        // type's zero default rather than approximately zero, so this must be exact equality.
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (value.Equals(0d))
            return;
        WriteTag(fieldNumber, 1);
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    public void WriteFloat(int fieldNumber, float value)
    {
        // Intentional exact comparison: proto3 wire format omits scalar fields equal to the
        // type's zero default rather than approximately zero, so this must be exact equality.
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (value.Equals(0f))
            return;
        WriteTag(fieldNumber, 5);
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteString(int fieldNumber, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        WriteTag(fieldNumber, 2);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteRawVarint((uint)byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        _position += byteCount;
    }

    public void WriteBytes(int fieldNumber, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;
        WriteTag(fieldNumber, 2);
        WriteRawVarint((uint)value.Length);
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_position));
        _position += value.Length;
    }

    // ── Oneof member writers (explicit presence) ─────────────
    //
    // proto3 oneof members carry explicit presence: the serialized member is what
    // selects the oneof arm, so a member set to its default value (0, 0.0, false,
    // "") MUST still be emitted. The default-skipping writers above are correct for
    // ordinary proto3 fields but would silently turn such values into "no member
    // set" (decoded as null). Use these variants for oneof members.

    public void WriteUInt32Always(int fieldNumber, uint value)
    {
        WriteTag(fieldNumber, 0);
        WriteRawVarint(value);
    }

    public void WriteInt64Always(int fieldNumber, long value)
    {
        WriteTag(fieldNumber, 0);
        WriteRawVarint((ulong)value);
    }

    public void WriteUInt64Always(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, 0);
        WriteRawVarint(value);
    }

    public void WriteSInt32Always(int fieldNumber, int value)
    {
        WriteTag(fieldNumber, 0);
        WriteRawVarint(ZigZagEncode32(value));
    }

    public void WriteBoolAlways(int fieldNumber, bool value)
    {
        WriteTag(fieldNumber, 0);
        EnsureCapacity(1);
        _buffer[_position++] = value ? (byte)1 : (byte)0;
    }

    public void WriteDoubleAlways(int fieldNumber, double value)
    {
        WriteTag(fieldNumber, 1);
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    public void WriteFloatAlways(int fieldNumber, float value)
    {
        WriteTag(fieldNumber, 5);
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void WriteStringAlways(int fieldNumber, string? value)
    {
        WriteTag(fieldNumber, 2);
        if (string.IsNullOrEmpty(value))
        {
            WriteRawVarint(0u);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteRawVarint((uint)byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        _position += byteCount;
    }

    // ── Packed repeated fields ────────────────────────────────

    public void WritePackedUInt32(int fieldNumber, ReadOnlySpan<uint> values)
    {
        if (values.IsEmpty)
            return;

        // Write to a temp buffer to get the byte length
        var inner = new ProtobufWriter(values.Length * 5);
        foreach (uint v in values)
            inner.WriteRawVarint(v);

        WriteTag(fieldNumber, 2);
        WriteRawVarint((uint)inner.Position);
        EnsureCapacity(inner.Position);
        inner.WrittenMemory.Span.CopyTo(_buffer.AsSpan(_position));
        _position += inner.Position;
        inner.Dispose();
    }

    public void WritePackedUInt64(int fieldNumber, ReadOnlySpan<ulong> values)
    {
        if (values.IsEmpty)
            return;

        var inner = new ProtobufWriter(values.Length * 10);
        foreach (ulong v in values)
            inner.WriteRawVarint(v);

        WriteTag(fieldNumber, 2);
        WriteRawVarint((uint)inner.Position);
        EnsureCapacity(inner.Position);
        inner.WrittenMemory.Span.CopyTo(_buffer.AsSpan(_position));
        _position += inner.Position;
        inner.Dispose();
    }

    public void WritePackedSInt64(int fieldNumber, ReadOnlySpan<long> values)
    {
        if (values.IsEmpty)
            return;

        var inner = new ProtobufWriter(values.Length * 10);
        foreach (long v in values)
            inner.WriteRawVarint(ZigZagEncode64(v));

        WriteTag(fieldNumber, 2);
        WriteRawVarint((uint)inner.Position);
        EnsureCapacity(inner.Position);
        inner.WrittenMemory.Span.CopyTo(_buffer.AsSpan(_position));
        _position += inner.Position;
        inner.Dispose();
    }

    // ── Sub-message support ───────────────────────────────────

    public void WriteMessage(int fieldNumber, ref ProtobufWriter sub)
    {
        if (sub.Position == 0)
            return;
        WriteTag(fieldNumber, 2);
        WriteRawVarint((uint)sub.Position);
        EnsureCapacity(sub.Position);
        sub.WrittenMemory.Span.CopyTo(_buffer.AsSpan(_position));
        _position += sub.Position;
    }

    // ── Helpers ───────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ZigZagEncode32(int value) => (uint)((value << 1) ^ (value >> 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ZigZagEncode64(long value) => (ulong)((value << 1) ^ (value >> 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int additionalBytes)
    {
        int required = _position + additionalBytes;
        if (required <= _buffer.Length)
            return;

        int newSize = Math.Max(_buffer.Length * 2, required);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _position).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
