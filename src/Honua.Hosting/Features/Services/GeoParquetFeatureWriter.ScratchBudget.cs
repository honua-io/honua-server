// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.Security;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    internal sealed class ScratchBudget(long maxBytes)
    {
        private long _bytes;
        private readonly long _maxBytes = maxBytes;
        public bool LimitExceeded { get; private set; }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "ScratchStream owns the file on success; the catch disposes it if ownership transfer fails.")]
        internal Stream Create()
        {
            var stream = PrivateTempFile.CreateScratch("honua-hilbert-", null, 64 * 1024);
            try
            {
                return new ScratchStream(this, stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private sealed class ScratchStream(ScratchBudget budget, Stream inner) : Stream
        {
            private bool _disposed;
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override int Read(Span<byte> buffer) => inner.Read(buffer);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void SetLength(long value)
            {
                Reserve(value);
                Resize(() => inner.SetLength(value));
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Reserve(checked(Position + count));
                Resize(() => inner.Write(buffer, offset, count));
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Reserve(checked(Position + buffer.Length));
                var previous = inner.Length;
                try
                {
                    inner.Write(buffer);
                }
                finally
                {
                    budget._bytes += inner.Length - previous;
                }
            }

            private void Reserve(long end)
            {
                if (Math.Max(0, end - inner.Length) > budget._maxBytes - budget._bytes)
                {
                    budget.LimitExceeded = true;
                    throw new GeoParquetLimitExceededException();
                }
            }

            private void Resize(Action action)
            {
                var previous = inner.Length;
                try { action(); }
                finally { budget._bytes += inner.Length - previous; }
            }

            public override void Close()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    budget._bytes -= inner.Length;
                    try
                    {
                        inner.Dispose();
                    }
                    catch (IOException ex) when (IsStorageFull(ex))
                    {
                        // The failed write is translated by the session; close still deletes the file.
                    }
                }
                base.Close();
            }
        }
    }
}
