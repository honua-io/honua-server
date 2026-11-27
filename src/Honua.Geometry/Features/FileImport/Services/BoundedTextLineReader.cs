// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Core.Features.FileImport.Services;

internal sealed class BoundedTextLineReader
{
    private const int ChunkSize = 4096;

    private readonly StreamReader _reader;
    private readonly char[] _buffer = new char[ChunkSize];
    private int _position;
    private int _length;

    public BoundedTextLineReader(StreamReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<(string? Line, bool ExceededLimit)> ReadLineAsync(
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes < 0)
        {
            return (null, true);
        }

        StringBuilder? builder = null;
        long totalBytes = 0;
        var sawData = false;

        while (true)
        {
            if (_position >= _length)
            {
                _position = 0;
                _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_length == 0)
                {
                    return sawData ? (builder?.ToString() ?? string.Empty, false) : (null, false);
                }
            }

            sawData = true;
            var span = _buffer.AsSpan(_position, _length - _position);
            var newlineIndex = span.IndexOfAny('\r', '\n');
            var segment = newlineIndex >= 0 ? span[..newlineIndex] : span;

            var segmentBytes = Encoding.UTF8.GetByteCount(segment);
            if (segmentBytes > maxBytes - totalBytes)
            {
                return (null, true);
            }

            totalBytes += segmentBytes;
            if (newlineIndex >= 0)
            {
                var line = builder is null ? new string(segment) : builder.Append(segment).ToString();
                var terminator = span[newlineIndex];
                _position += newlineIndex + 1;
                if (terminator == '\r')
                {
                    await ConsumeFollowingLineFeedAsync(cancellationToken).ConfigureAwait(false);
                }

                return (line, false);
            }

            builder ??= new StringBuilder(segment.Length);
            builder.Append(segment);
            _position = _length;
        }
    }

    private async Task ConsumeFollowingLineFeedAsync(CancellationToken cancellationToken)
    {
        if (_position >= _length)
        {
            _position = 0;
            _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        if (_position < _length && _buffer[_position] == '\n')
        {
            _position++;
        }
    }
}
