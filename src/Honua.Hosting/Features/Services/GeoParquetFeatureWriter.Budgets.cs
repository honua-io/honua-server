// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    private static void ValidateEncodingLimits(GeoParquetLimits limits)
    {
        if (limits.MaxRowsPerBatch is < 1 or > 10000 || limits.MaxEstimatedBatchBytes < 1 ||
            limits.MaxEstimatedInputBytes < 1 || limits.MaxResponseBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "GeoParquet encoding budgets must be positive and batches cannot exceed 10000 rows.");
        }
    }

    private static List<(int Offset, int Count)> PlanBatches(
        ImmutableArray<Feature> features, GeoParquetLimits limits, int columnCount = 0)
    {
        var batches = new List<(int Offset, int Count)>();
        long totalBytes = 0;
        long batchBytes = 0;
        var offset = 0;
        var count = 0;
        for (var index = 0; index < features.Length; index++)
        {
            var feature = features[index];
            // Conservative admission estimate, not a process-RSS guarantee: allow space for
            // WKB decoding/copies, UTF-16 plus UTF-8 buffers and fixed per-cell bookkeeping.
            var bytes = 128L + 32L * columnCount + 6L * (feature.Geometry?.Length ?? 0);
            foreach (var (name, value) in feature.Attributes)
            {
                bytes += 64L + 2L * name.Length + (value switch
                {
                    string text => 6L * text.Length,
                    byte[] binary => 2L * binary.Length,
                    JsonElement { ValueKind: JsonValueKind.Undefined } => 64L,
                    JsonElement json => 6L * json.GetRawText().Length,
                    _ => 64L
                });
            }

            totalBytes += bytes;
            if (bytes > limits.MaxEstimatedBatchBytes || totalBytes > limits.MaxEstimatedInputBytes)
            {
                throw new GeoParquetLimitExceededException();
            }

            if (count > 0 && (count == limits.MaxRowsPerBatch || batchBytes + bytes > limits.MaxEstimatedBatchBytes))
            {
                batches.Add((offset, count));
                offset = index;
                count = 0;
                batchBytes = 0;
            }
            count++;
            batchBytes += bytes;
        }
        if (count > 0)
        {
            batches.Add((offset, count));
        }
        return batches;
    }

    // A full scratch volume is transient: concurrent spools drain within seconds once their
    // readers finish, so tell the caller to retry instead of reporting a query failure.
    internal const int ScratchFullRetryAfterSeconds = 10;

    internal const string ScratchFullClientMessage =
        "The server's temporary storage is full. Retry the GeoParquet request shortly.";

    // ENOSPC and EDQUOT (Linux), ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL (Windows HRESULTs).
    private static readonly int[] StorageFullErrorCodes = [28, 122, unchecked((int)0x80070027), unchecked((int)0x80070070)];

    internal static bool IsStorageFull(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException && Array.IndexOf(StorageFullErrorCodes, current.HResult) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // The encoder's spool: a private delete-on-close scratch file in production. It refuses
    // writes past the response budget and records when the scratch volume itself is full.
    internal sealed class BoundedParquetStream : Stream
    {
        private const int WriteBufferBytes = 64 * 1024;

        private readonly int maxBytes;
        private readonly Stream spool;

        public BoundedParquetStream(int maxBytes)
            : this(maxBytes, PrivateTempFile.CreateScratch("honua-parquet-", extension: null, WriteBufferBytes))
        {
        }

        internal BoundedParquetStream(int maxBytes, Stream spool)
        {
            this.maxBytes = maxBytes;
            this.spool = spool;
        }

        public bool LimitExceeded { get; private set; }

        // The scratch file behind the payload, for tests that assert it is deleted on dispose.
        internal string? ScratchPath => (spool as FileStream)?.Name;

        // Set when the scratch volume refused a write. The native encoder can wrap the managed
        // IOException, so the writer reads this flag rather than the exception it receives.
        public bool StorageFull { get; private set; }

        public override bool CanRead => spool.CanRead;

        public override bool CanSeek => spool.CanSeek;

        public override bool CanWrite => spool.CanWrite;

        public override long Length => spool.Length;

        public override long Position
        {
            get => spool.Position;
            set => Spool(() => spool.Position = value);
        }

        private void Reserve(long end)
        {
            if (end > maxBytes)
            {
                LimitExceeded = true;
                throw new GeoParquetLimitExceededException();
            }
        }

        private void Spool(Action write)
        {
            try
            {
                write();
            }
            catch (IOException ex) when (IsStorageFull(ex))
            {
                StorageFull = true;
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => spool.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => spool.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => spool.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = 0;
            Spool(() => position = spool.Seek(offset, origin));
            return position;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(Position + count);
            Spool(() => spool.Write(buffer, offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(Position + buffer.Length);
            try
            {
                spool.Write(buffer);
            }
            catch (IOException ex) when (IsStorageFull(ex))
            {
                StorageFull = true;
                throw;
            }
        }

        public override void WriteByte(byte value)
        {
            Reserve(Position + 1);
            Spool(() => spool.WriteByte(value));
        }

        public override void SetLength(long value)
        {
            Reserve(value);
            Spool(() => spool.SetLength(value));
        }

        public override void Flush() => Spool(spool.Flush);

        // Stream.Dispose and DisposeAsync both end here, so the scratch file always goes.
        // A buffered spool retries refused bytes on close; the runtime still closes (and so
        // deletes) the file, and the refusal is already reported as a retryable 503.
        public override void Close()
        {
            try
            {
                spool.Dispose();
            }
            catch (IOException ex) when (IsStorageFull(ex))
            {
                StorageFull = true;
            }

            base.Close();
        }
    }
}
