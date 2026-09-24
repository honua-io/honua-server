// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Infrastructure.Services;

/// <summary>Encoding admission and output-buffer bounds for the shared GeoParquet writer.</summary>
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

    private sealed class BoundedParquetStream(int maxBytes) : MemoryStream
    {
        public bool LimitExceeded { get; private set; }

        private void Reserve(long end)
        {
            if (end > maxBytes)
            {
                LimitExceeded = true;
                throw new GeoParquetLimitExceededException();
            }
            if (end > Capacity)
            {
                // MemoryStream normally doubles capacity past the requested size. Clamp the
                // capacity itself, so a successful payload cannot retain more than the budget.
                Capacity = (int)Math.Min(maxBytes, Math.Max(end, Math.Max(256L, Capacity * 2L)));
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(Position + count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(Position + buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Reserve(Position + 1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            Reserve(value);
            base.SetLength(value);
        }
    }
}
