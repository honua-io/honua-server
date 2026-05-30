// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Minimal protobuf wire-format writer for Esri PBF query responses.
/// Zero-allocation hot path — writes directly to a pooled buffer.
/// No external protobuf library dependency; fully AOT-compatible.
/// </summary>
/// <remarks>
/// Reference: https://protobuf.dev/programming-guides/encoding/
/// Wire types: 0=varint, 1=64-bit, 2=length-delimited, 5=32-bit
/// </remarks>
internal ref struct ProtobufWriter
{
    private byte[] _buffer;
    private int _position;

    /// <summary>
    /// Creates a new writer with an initial buffer from the shared pool.
    /// </summary>
    public ProtobufWriter(int initialCapacity = 4096)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _position = 0;
    }

    /// <summary>Current write position.</summary>
    public readonly int Position => _position;

    /// <summary>
    /// Returns the written bytes as a new array and returns the internal buffer to the pool.
    /// </summary>
    public byte[] ToArrayAndDispose()
    {
        var result = _buffer.AsSpan(0, _position).ToArray();
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
        _position = 0;
        return result;
    }

    /// <summary>
    /// Returns the written bytes as a ReadOnlyMemory. Caller must call Dispose() after use.
    /// </summary>
    public readonly ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _position);

    /// <summary>
    /// Returns the pooled buffer. Must be called if ToArrayAndDispose was not used.
    /// </summary>
    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
    }

    // ── Tag writing ───────────────────────────────────────────

    /// <summary>Writes a field tag (field number + wire type).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTag(int fieldNumber, int wireType)
    {
        WriteRawVarint((uint)((fieldNumber << 3) | wireType));
    }

    // ── Varint encoding ───────────────────────────────────────

    /// <summary>Writes an unsigned varint.</summary>
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

    /// <summary>Writes a 64-bit unsigned varint.</summary>
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

    /// <summary>Writes a uint32 field (wire type 0).</summary>
    public void WriteUInt32(int fieldNumber, uint value)
    {
        if (value == 0)
            return; // proto3 default
        WriteTag(fieldNumber, 0);
        WriteRawVarint(value);
    }

    /// <summary>Writes an int64 field (wire type 0).</summary>
    public void WriteInt64(int fieldNumber, long value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint((ulong)value);
    }

    /// <summary>Writes a uint64 field (wire type 0).</summary>
    public void WriteUInt64(int fieldNumber, ulong value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint(value);
    }

    /// <summary>Writes a sint32 field using zigzag encoding (wire type 0).</summary>
    public void WriteSInt32(int fieldNumber, int value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint(ZigZagEncode32(value));
    }

    /// <summary>Writes a sint64 field using zigzag encoding (wire type 0).</summary>
    public void WriteSInt64(int fieldNumber, long value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint(ZigZagEncode64(value));
    }

    /// <summary>Writes a bool field (wire type 0).</summary>
    public void WriteBool(int fieldNumber, bool value)
    {
        if (!value)
            return; // proto3 default
        WriteTag(fieldNumber, 0);
        EnsureCapacity(1);
        _buffer[_position++] = 1;
    }

    /// <summary>Writes an enum field (wire type 0).</summary>
    public void WriteEnum(int fieldNumber, int value)
    {
        if (value == 0)
            return;
        WriteTag(fieldNumber, 0);
        WriteRawVarint((uint)value);
    }

    /// <summary>Writes a double field (wire type 1).</summary>
    public void WriteDouble(int fieldNumber, double value)
    {
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (value == 0.0)
            return;
        WriteTag(fieldNumber, 1);
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>Writes a float field (wire type 5).</summary>
    public void WriteFloat(int fieldNumber, float value)
    {
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (value == 0.0f)
            return;
        WriteTag(fieldNumber, 5);
        EnsureCapacity(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    /// <summary>Writes a string field (wire type 2).</summary>
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

    /// <summary>Writes a bytes field (wire type 2).</summary>
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

    // ── Packed repeated fields ────────────────────────────────

    /// <summary>Writes a packed repeated uint32 field.</summary>
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

    /// <summary>Writes a packed repeated sint64 field.</summary>
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

    /// <summary>
    /// Writes a length-delimited sub-message. The caller writes the
    /// sub-message into a separate ProtobufWriter, then calls this.
    /// </summary>
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
