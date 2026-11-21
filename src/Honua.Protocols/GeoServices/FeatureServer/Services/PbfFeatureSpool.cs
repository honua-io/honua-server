// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using Honua.Core.Exceptions;
using Honua.Core.Features.Security;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

// Single-pass bounded spool for encoded Esri PBF Feature entries. Small responses stay in a
// pooled memory buffer with zero disk I/O; larger responses spill to a private delete-on-close
// scratch file so neither the materialized Feature page nor the encoded PBF payload pins
// managed heap while a slow client drains.
internal sealed class PbfFeatureSpool : IDisposable
{
    internal const int DefaultMemorySpillThresholdBytes = 64 * 1024;
    internal const int StreamChunkBytes = 64 * 1024;
    internal const int ScratchFullRetryAfterSeconds = 10;
    internal const string ScratchFullClientMessage =
        "The server's temporary storage is full. Retry the PBF request shortly.";

    private readonly int _memorySpillThresholdBytes;
    private readonly Func<Stream> _scratchFactory;

    private byte[]? _memoryBuffer;
    private int _memoryLength;
    private Stream? _scratchStream;
    private bool _usedScratchFile;
    private string? _scratchPath;
    private long _length;
    private bool _disposed;

    public PbfFeatureSpool(int memorySpillThresholdBytes = DefaultMemorySpillThresholdBytes)
        : this(memorySpillThresholdBytes, CreateDefaultScratchStream)
    {
    }

    internal PbfFeatureSpool(int memorySpillThresholdBytes, Func<Stream> scratchFactory)
    {
        if (memorySpillThresholdBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memorySpillThresholdBytes),
                "Memory spill threshold must be non-negative.");
        }

        _memorySpillThresholdBytes = memorySpillThresholdBytes;
        _scratchFactory = scratchFactory ?? throw new ArgumentNullException(nameof(scratchFactory));
    }

    public long Length => _length;

    internal bool UsedScratchFile => _usedScratchFile;

    internal string? ScratchPath => _scratchPath;

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty)
        {
            return;
        }

        if (_scratchStream is not null)
        {
            WriteToScratch(_scratchStream, data);
            _length = checked(_length + data.Length);
            return;
        }

        var requiredMemory = checked((long)_memoryLength + data.Length);
        if (requiredMemory <= _memorySpillThresholdBytes)
        {
            EnsureMemoryCapacity((int)requiredMemory);
            data.CopyTo(_memoryBuffer!.AsSpan(_memoryLength));
            _memoryLength += data.Length;
            _length = checked(_length + data.Length);
            return;
        }

        SpillToScratch(data);
        _length = checked(_length + data.Length);
    }

    public void CompleteWriting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_scratchStream is not null)
        {
            SeekScratchToStart(_scratchStream);
        }
    }

    public Task WriteToAsync(Stream output, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);

        if (_length == 0)
        {
            return Task.CompletedTask;
        }

        return _scratchStream is null
            ? WriteMemoryToAsync(output, cancellationToken)
            : WriteScratchToAsync(_scratchStream, output, cancellationToken);
    }

    private async Task WriteMemoryToAsync(Stream output, CancellationToken cancellationToken)
    {
        var remaining = _memoryBuffer.AsMemory(0, _memoryLength);
        while (!remaining.IsEmpty)
        {
            var chunk = Math.Min(StreamChunkBytes, remaining.Length);
            await output.WriteAsync(remaining[..chunk], cancellationToken).ConfigureAwait(false);
            remaining = remaining[chunk..];
        }
    }

    private static async Task WriteScratchToAsync(Stream scratch, Stream output, CancellationToken cancellationToken)
    {
        SeekScratchToStart(scratch);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamChunkBytes);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await scratch.ReadAsync(buffer.AsMemory(0, StreamChunkBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var buffer = _memoryBuffer;
        _memoryBuffer = null;
        _memoryLength = 0;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var scratch = _scratchStream;
        _scratchStream = null;
        if (scratch is not null)
        {
            try
            {
                scratch.Dispose();
            }
            catch (IOException ex) when (PrivateTempFile.IsStorageFull(ex))
            {
                // A buffered scratch stream may retry refused bytes on close; the OS still
                // closes and deletes the handle, and the caller already received 503.
            }
        }
    }

    private static Stream CreateDefaultScratchStream()
        => PrivateTempFile.CreateScratch("honua-pbf-", extension: null, StreamChunkBytes);

    private void EnsureMemoryCapacity(int requiredCapacity)
    {
        if (_memoryBuffer is not null && _memoryBuffer.Length >= requiredCapacity)
        {
            return;
        }

        var nextCapacity = _memoryBuffer is null
            ? Math.Min(Math.Max(requiredCapacity, 4096), _memorySpillThresholdBytes)
            : (int)Math.Min(Math.Max((long)requiredCapacity, (long)_memoryBuffer.Length * 2), _memorySpillThresholdBytes);
        var next = ArrayPool<byte>.Shared.Rent(nextCapacity);
        if (_memoryBuffer is not null)
        {
            if (_memoryLength > 0)
            {
                _memoryBuffer.AsSpan(0, _memoryLength).CopyTo(next);
            }

            ArrayPool<byte>.Shared.Return(_memoryBuffer);
        }

        _memoryBuffer = next;
    }

    private void SpillToScratch(ReadOnlySpan<byte> trailingData)
    {
        Stream scratch;
        try
        {
            scratch = _scratchFactory();
        }
        catch (IOException ex) when (PrivateTempFile.IsStorageFull(ex))
        {
            throw new ServiceUnavailableException(ScratchFullClientMessage, ScratchFullRetryAfterSeconds);
        }

        _scratchStream = scratch;
        _usedScratchFile = true;
        _scratchPath = (scratch as FileStream)?.Name;
        try
        {
            if (_memoryBuffer is not null && _memoryLength > 0)
            {
                WriteToScratch(scratch, _memoryBuffer.AsSpan(0, _memoryLength));
            }

            WriteToScratch(scratch, trailingData);
        }
        catch
        {
            Dispose();
            throw;
        }

        var buffer = _memoryBuffer;
        _memoryBuffer = null;
        _memoryLength = 0;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteToScratch(Stream scratch, ReadOnlySpan<byte> data)
    {
        try
        {
            scratch.Write(data);
        }
        catch (IOException ex) when (PrivateTempFile.IsStorageFull(ex))
        {
            throw new ServiceUnavailableException(ScratchFullClientMessage, ScratchFullRetryAfterSeconds);
        }
    }

    private static void SeekScratchToStart(Stream scratch)
    {
        try
        {
            scratch.Flush();
            scratch.Seek(0, SeekOrigin.Begin);
        }
        catch (IOException ex) when (PrivateTempFile.IsStorageFull(ex))
        {
            throw new ServiceUnavailableException(ScratchFullClientMessage, ScratchFullRetryAfterSeconds);
        }
    }
}
