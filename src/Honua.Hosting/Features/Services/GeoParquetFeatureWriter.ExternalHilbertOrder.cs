// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.FeatureStore.Domain;
using NetTopologySuite.Geometries;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    // Three files bound descriptor usage regardless of input size: feature rows plus
    // ping-pong indexes. Only one feature and a bounded run of 24-byte keys are decoded.
    private sealed class ExternalHilbertOrder(StreamingEncoding encoding) : IDisposable
    {
        private const int IndexEntryBytes = 24;
        private Stream? _features;
        private Stream? _index;
        private Stream? _merge;
        private long _count;
        private readonly Envelope _extent = new();
        public ScratchBudget Scratch { get; } = new(encoding.Limits.MaxScratchBytes);

        public async Task PrepareAsync(IAsyncEnumerable<Feature> source, Action<Feature> inspect, CancellationToken cancellationToken)
        {
            _features = Scratch.Create();
            _index = Scratch.Create();
            _merge = Scratch.Create();
            using var writer = new BinaryWriter(_features, Encoding.UTF8, leaveOpen: true);
            await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EstimateFeatureBytes(feature, encoding.Resource.SchemaFields.Count + 6)
                    > Math.Min(encoding.Limits.MaxEstimatedBatchBytes, encoding.Limits.MaxEstimatedInputBytes))
                {
                    throw new GeoParquetLimitExceededException();
                }
                inspect(feature);
                var envelope = EnvelopeFor(feature);
                if (HasFiniteEnvelope(envelope))
                {
                    _extent.ExpandToInclude(envelope);
                }
                WriteScratchFeature(writer, feature, encoding);
                _count++;
            }
            writer.Flush();
            BuildSortedRuns(cancellationToken);
            MergeRuns(cancellationToken);
        }

        private Envelope? EnvelopeFor(Feature feature)
        {
            var (_, envelope, _, _) = ProcessGeometryCore(feature.Geometry, encoding.Srid,
                encoding.Options.GeometryLimits, encoding.Options.ReturnZ, encoding.Options.ReturnM,
                feature.Id, encoding.Options.Logger, encodeWkb: false);
            return envelope;
        }

        private int RunLength => (int)Math.Min(encoding.Limits.MaxRowsPerBatch,
            Math.Max(1, Math.Min(encoding.Limits.MaxEstimatedBatchBytes,
                encoding.Limits.MaxEstimatedInputBytes) / IndexEntryBytes));

        private void BuildSortedRuns(CancellationToken cancellationToken)
        {
            _features!.Position = 0;
            using var reader = new BinaryReader(_features, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(_index!, Encoding.UTF8, leaveOpen: true);
            var run = new List<SortEntry>(RunLength);
            for (long sequence = 0; sequence < _count; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = _features.Position;
                var feature = ReadScratchFeature(reader);
                run.Add(new SortEntry(HilbertKey(EnvelopeFor(feature), _extent), sequence, offset));
                if (run.Count == RunLength || sequence == _count - 1)
                {
                    run.Sort(CompareEntries);
                    foreach (var entry in run)
                    {
                        WriteEntry(writer, entry);
                    }
                    run.Clear();
                }
            }
        }

        private void MergeRuns(CancellationToken cancellationToken)
        {
            for (long width = RunLength; width < _count; width *= 2)
            {
                _merge!.SetLength(0);
                _merge.Position = 0;
                using var reader = new BinaryReader(_index!, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(_merge, Encoding.UTF8, leaveOpen: true);
                for (long start = 0; start < _count; start += 2 * width)
                {
                    MergePair(new MergeContext(reader, writer, cancellationToken) { Width = width }, start);
                }
                writer.Flush();
                (_index, _merge) = (_merge, _index);
            }
            _merge!.SetLength(0);
        }

        private readonly record struct MergeContext(BinaryReader Reader, BinaryWriter Writer, CancellationToken Cancellation)
        {
            public long Width { get; init; }
        }

        private void MergePair(MergeContext context, long start)
        {
            var (reader, writer, cancellationToken) = context;
            var width = context.Width;
            var left = start;
            var right = Math.Min(start + width, _count);
            var leftEnd = right;
            var rightEnd = Math.Min(start + 2 * width, _count);
            var bufferLength = Math.Max(1, RunLength / 2);
            var leftCursor = new MergeCursor(reader, bufferLength);
            var rightCursor = new MergeCursor(reader, bufferLength);
            SortEntry? leftEntry = null;
            SortEntry? rightEntry = null;
            while (left < leftEnd || right < rightEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                leftEntry ??= left < leftEnd ? leftCursor.Read(left, leftEnd) : null;
                rightEntry ??= right < rightEnd ? rightCursor.Read(right, rightEnd) : null;
                if (rightEntry is null || (leftEntry is not null && CompareEntries(leftEntry.Value, rightEntry.Value) <= 0))
                {
                    WriteEntry(writer, leftEntry!.Value);
                    leftEntry = null;
                    left++;
                }
                else
                {
                    WriteEntry(writer, rightEntry.Value);
                    rightEntry = null;
                    right++;
                }
            }
        }

        public async IAsyncEnumerable<Feature> ReadOrderedAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _index!.Position = 0;
            using var indexReader = new BinaryReader(_index, Encoding.UTF8, leaveOpen: true);
            using var featureReader = new BinaryReader(_features!, Encoding.UTF8, leaveOpen: true);
            for (long row = 0; row < _count; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = ReadEntry(indexReader);
                _features!.Position = entry.Offset;
                yield return ReadScratchFeature(featureReader);
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private readonly record struct SortEntry(ulong Key, long Sequence, long Offset);
        private static int CompareEntries(SortEntry left, SortEntry right)
        {
            var key = left.Key.CompareTo(right.Key);
            return key != 0 ? key : left.Sequence.CompareTo(right.Sequence);
        }

        // Independent bounded buffers prevent interleaved runs from seeking the shared
        // file stream for every 24-byte entry once runs exceed its native buffer.
        private sealed class MergeCursor(BinaryReader reader, int capacity)
        {
            private readonly SortEntry[] _buffer = new SortEntry[capacity];
            private int _position;
            private int _length;

            public SortEntry Read(long position, long end)
            {
                if (_position == _length)
                {
                    reader.BaseStream.Position = checked(position * IndexEntryBytes);
                    _length = (int)Math.Min(_buffer.Length, end - position);
                    for (var index = 0; index < _length; index++)
                    {
                        _buffer[index] = ReadEntry(reader);
                    }
                    _position = 0;
                }
                return _buffer[_position++];
            }
        }

        private static SortEntry ReadEntry(BinaryReader reader) => new(reader.ReadUInt64(), reader.ReadInt64(), reader.ReadInt64());
        private static void WriteEntry(BinaryWriter writer, SortEntry entry)
        {
            writer.Write(entry.Key);
            writer.Write(entry.Sequence);
            writer.Write(entry.Offset);
        }

        public void Dispose()
        {
            try { _features?.Dispose(); }
            finally
            {
                try { _index?.Dispose(); }
                finally { _merge?.Dispose(); }
            }
        }
    }
}
