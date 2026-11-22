// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Tiles;

public sealed class PooledTileData : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;
    private readonly bool _pooled;

    private PooledTileData(byte[] buffer, int length, bool pooled)
    {
        _buffer = buffer;
        _length = length;
        _pooled = pooled;
    }

    public int Length => _length;

    public ReadOnlyMemory<byte> Memory => _buffer is { } buffer
        ? buffer.AsMemory(0, _length)
        : ReadOnlyMemory<byte>.Empty;

    public ReadOnlySpan<byte> Span => _buffer is { } buffer
        ? buffer.AsSpan(0, _length)
        : ReadOnlySpan<byte>.Empty;

    public Memory<byte> WritableMemory
    {
        get
        {
            var buffer = Volatile.Read(ref _buffer)
                ?? throw new ObjectDisposedException(nameof(PooledTileData));
            return buffer.AsMemory(0, _length);
        }
    }

    public static PooledTileData Rent(int exactLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exactLength);
        if (exactLength == 0)
        {
            return new PooledTileData([], 0, pooled: false);
        }

        return new PooledTileData(ArrayPool<byte>.Shared.Rent(exactLength), exactLength, pooled: true);
    }

    public static PooledTileData FromOwnedArray(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PooledTileData(data, data.Length, pooled: false);
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null && _pooled)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

public readonly record struct MvtTileRequest
{
    public required int LayerId { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Z { get; init; }
    public FeatureQuery? Query { get; init; }
    public required TileOptions TileOptions { get; init; }
    public required TileLimits TileLimits { get; init; }
    public GridGeometry? GridGeometry { get; init; }
}
