// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.ZarrParser;
using Xunit;

namespace Honua.Core.Tests.Raster.ZarrParser;

public class ZarrSubsetReaderTests
{
    [Fact]
    public async Task ReadSubsetAsync_UncompressedSpansMultipleChunks_ReturnsContiguousRowMajor()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "datasets/temp",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => (float)r * 100 + c);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/temp");

        var request = new ZarrSubsetRequest
        {
            Variable = "temp",
            Start = new[] { 2, 2 },
            Stop = new[] { 6, 6 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/temp", metadata, request);

        subset.Shape.Should().Equal(4, 4);
        subset.DataType.Should().Be("<f4");
        subset.Data.Length.Should().Be(4 * 4 * sizeof(float));
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                var globalRow = r + 2;
                var globalCol = c + 2;
                var expected = (float)globalRow * 100 + globalCol;
                var actual = BitConverter.ToSingle(subset.Data, (r * 4 + c) * sizeof(float));
                actual.Should().Be(expected, $"cell ({r},{c}) should reflect global ({globalRow},{globalCol})");
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_ZlibCompressed_DecodesCorrectly()
    {
        var objects = ZarrFixtureBuilder.BuildGroupedZlib(
            root: "datasets/grid",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c * 0.5f,
            srid: 4326,
            xMin: 0,
            yMin: 0,
            xMax: 8,
            yMax: 8);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/grid");

        var request = new ZarrSubsetRequest
        {
            Variable = "temperature",
            Start = new[] { 0, 0 },
            Stop = new[] { 5, 5 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/grid", metadata, request);

        subset.Shape.Should().Equal(5, 5);
        for (var r = 0; r < 5; r++)
        {
            for (var c = 0; c < 5; c++)
            {
                var expected = r + c * 0.5f;
                var actual = BitConverter.ToSingle(subset.Data, (r * 5 + c) * sizeof(float));
                actual.Should().Be(expected);
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_V3UncompressedCPrefixedChunks_ReturnsContiguousRowMajor()
    {
        var objects = ZarrFixtureBuilder.BuildV3SingleArray(
            root: "datasets/v3temp",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => (float)r * 100 + c,
            gzip: false);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/v3temp");

        var request = new ZarrSubsetRequest
        {
            Variable = "v3temp",
            Start = new[] { 2, 2 },
            Stop = new[] { 6, 6 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/v3temp", metadata, request);

        subset.Shape.Should().Equal(4, 4);
        subset.DataType.Should().Be("<f4");
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                var globalRow = r + 2;
                var globalCol = c + 2;
                var expected = (float)globalRow * 100 + globalCol;
                var actual = BitConverter.ToSingle(subset.Data, (r * 4 + c) * sizeof(float));
                actual.Should().Be(expected, $"v3 cell ({r},{c}) should reflect global ({globalRow},{globalCol})");
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_V3GzipCoded_DecodesCorrectly()
    {
        var objects = ZarrFixtureBuilder.BuildV3SingleArray(
            root: "datasets/v3gzip",
            rows: 8,
            cols: 8,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => r + c * 0.5f,
            gzip: true);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/v3gzip");
        metadata.Arrays[0].Compressor.Should().Be("gzip");

        var request = new ZarrSubsetRequest
        {
            Variable = "v3gzip",
            Start = new[] { 0, 0 },
            Stop = new[] { 5, 5 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/v3gzip", metadata, request);

        subset.Shape.Should().Equal(5, 5);
        for (var r = 0; r < 5; r++)
        {
            for (var c = 0; c < 5; c++)
            {
                var expected = r + c * 0.5f;
                var actual = BitConverter.ToSingle(subset.Data, (r * 5 + c) * sizeof(float));
                actual.Should().Be(expected);
            }
        }
    }

    [Fact]
    public async Task ReadSubsetAsync_OutOfBounds_Throws()
    {
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "datasets/bounds",
            rows: 4,
            cols: 4,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => 0f);
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/bounds");

        var request = new ZarrSubsetRequest
        {
            Variable = "bounds",
            Start = new[] { 0, 0 },
            Stop = new[] { 5, 5 }
        };

        var act = () => new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/bounds", metadata, request);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// Regression test for BH-017: a Zarr store that declares chunk dimensions whose
    /// product × element-size exceeds MaxBytesPerRequest must be rejected before any
    /// per-chunk heap allocation is attempted.  With MaxDegreeOfParallelism=8, reading
    /// 8 such chunks would otherwise allocate ~2 GB of heap before the caller receives
    /// any error.
    /// </summary>
    [Fact]
    public async Task ReadSubsetAsync_OversizedChunkDimensions_ThrowsBeforeAllocatingChunkBuffer()
    {
        // shape=[4,4] but chunks=[8200,8200] with dtype=<f4:
        //   chunkBytes = 8200 * 8200 * 4 = 268,960,000 > MaxBytesPerRequest (256 MiB)
        // The array total (4*4*4=64 bytes) is well under the total-bytes cap so
        // only the per-chunk guard added by BH-017 stops the allocation.
        var objects = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["datasets/huge/.zarray"] = System.Text.Encoding.UTF8.GetBytes(
                """{"chunks":[8200,8200],"compressor":null,"dtype":"<f4","fill_value":0,"filters":null,"order":"C","shape":[4,4],"zarr_format":2}""")
        };
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/huge");

        var request = new ZarrSubsetRequest
        {
            Variable = "huge",
            Start = new[] { 0, 0 },
            Stop = new[] { 4, 4 }
        };

        var act = () => new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/huge", metadata, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*chunk size*exceeds*");
    }

    /// <summary>
    /// Regression test for BH-017: ResolveElementSize must reject data types outside
    /// the executable direct-read matrix before any array-wide computation occurs.
    /// </summary>
    [Fact]
    public void ResolveElementSize_ElementSizeExceedsMaxSupported_Throws()
    {
        var act = () => ZarrSubsetReader.ResolveElementSize("<f32");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*support matrix*");
    }

    /// <summary>
    /// Regression test for BH2-015: WriteFillScalar had no case for &lt;i8 (int64) so
    /// a missing chunk was zero-filled instead of using the declared fill_value.
    /// After the fix the chunk buffer must be filled with the correct little-endian
    /// representation of the fill value.
    /// </summary>
    [Fact]
    public async Task ReadSubsetAsync_Int64DtypeWithNonzeroFillValue_MissingChunkFillsCorrectly()
    {
        // Build a 4×4 <i8 array with fill_value=-9999. We deliberately omit the chunk
        // bytes from the in-memory store so the reader must fall back to FillWithFillValue.
        const long fillValue = -9999L;
        var objects = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["datasets/int64/.zarray"] = System.Text.Encoding.UTF8.GetBytes(
                """{"chunks":[4,4],"compressor":null,"dtype":"<i8","fill_value":-9999,"filters":null,"order":"C","shape":[4,4],"zarr_format":2}""")
            // No chunk key "datasets/int64/0.0" — reader must use fill value.
        };
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/int64");

        var request = new ZarrSubsetRequest
        {
            Variable = "int64",
            Start = new[] { 0, 0 },
            Stop = new[] { 4, 4 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/int64", metadata, request);

        subset.Shape.Should().Equal(4, 4);
        subset.DataType.Should().Be("<i8");
        subset.Data.Length.Should().Be(4 * 4 * sizeof(long));

        // Every cell in the missing chunk must carry the declared fill value, not 0.
        for (var i = 0; i < 16; i++)
        {
            var actual = BitConverter.ToInt64(subset.Data, i * sizeof(long));
            actual.Should().Be(fillValue, $"cell {i} should be filled with {fillValue} (BH2-015)");
        }
    }

    // ─── BH3-022 regressions ──────────────────────────────────────────────────────

    /// <summary>
    /// Regression test for BH3-022: the read ceiling passed to ICloudRangeReader.ReadRangeAsync
    /// must be the metadata-declared chunk byte count, not int.MaxValue/4 (~512 MB).
    /// Before the fix, eight parallel 512 MB reads could allocate ~4.3 GB of temporary
    /// buffers for a request where the per-request cap is 256 MiB.
    /// After the fix the ceiling equals chunkBytes so the allocation is bounded.
    /// </summary>
    [Fact]
    public async Task ReadSubsetAsync_UncompressedChunk_ReadLengthBoundedToChunkBytes()
    {
        // 4×4 chunk of <f4: chunkBytes = 4 * 4 * 4 = 64 bytes.
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "datasets/ceiling",
            rows: 4,
            cols: 4,
            chunkRows: 4,
            chunkCols: 4,
            sample: (r, c) => (float)r * 4 + c);
        var trackingReader = new TrackingZarrRangeReader(new InMemoryZarrRangeReader(objects));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(
            new InMemoryZarrRangeReader(objects), "bucket", "datasets/ceiling");

        var request = new ZarrSubsetRequest
        {
            Variable = "ceiling",
            Start = new[] { 0, 0 },
            Stop = new[] { 4, 4 }
        };

        await new ZarrSubsetReader().ReadSubsetAsync(
            trackingReader, "bucket", "datasets/ceiling", metadata, request);

        const int expectedChunkBytes = 4 * 4 * sizeof(float); // 64 bytes
        trackingReader.RecordedLengths.Should().AllSatisfy(length =>
            length.Should().Be(expectedChunkBytes,
                "read ceiling must equal the declared chunk byte count (BH3-022), not int.MaxValue/4"));
    }

    /// <summary>
    /// Regression test for BH3-022: when an attacker-controlled Zarr store writes a chunk
    /// object that is larger than the declared chunk size, only <c>chunkBytes</c> bytes should
    /// be consumed (the ICloudRangeReader implementation returns at most <c>length</c> bytes).
    /// The read completes successfully and returns the correct subset values.
    /// </summary>
    [Fact]
    public async Task ReadSubsetAsync_ChunkObjectLargerThanDeclaredSize_ReadsOnlyDeclaredBytes()
    {
        // Build a 2×2 <f4 store with 2×2 chunks (chunkBytes = 16 bytes).
        var objects = ZarrFixtureBuilder.BuildSingleVariableUncompressed(
            root: "datasets/oversized",
            rows: 2,
            cols: 2,
            chunkRows: 2,
            chunkCols: 2,
            sample: (r, c) => (float)r * 2 + c + 1); // values: 1, 2, 3, 4

        // Bloat the single chunk object to 1 MB (far beyond the 16-byte declared size).
        // An attacker-controlled store could do this to exploit the old int.MaxValue/4 ceiling.
        var bloatedData = new byte[1024 * 1024];
        var originalChunk = objects["datasets/oversized/0.0"];
        Array.Copy(originalChunk, bloatedData, originalChunk.Length);
        objects["datasets/oversized/0.0"] = bloatedData;

        var trackingReader = new TrackingZarrRangeReader(new InMemoryZarrRangeReader(objects));
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(
            new InMemoryZarrRangeReader(objects), "bucket", "datasets/oversized");

        var request = new ZarrSubsetRequest
        {
            Variable = "oversized",
            Start = new[] { 0, 0 },
            Stop = new[] { 2, 2 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(
            trackingReader, "bucket", "datasets/oversized", metadata, request);

        const int expectedChunkBytes = 2 * 2 * sizeof(float); // 16 bytes
        trackingReader.RecordedLengths.Should().AllSatisfy(length =>
            length.Should().Be(expectedChunkBytes,
                "read ceiling must be capped at the declared chunk byte count even when the cloud object is larger"));

        // Data integrity: the first 16 bytes are correct despite the bloated object.
        subset.Shape.Should().Equal(2, 2);
        for (var r = 0; r < 2; r++)
        {
            for (var c = 0; c < 2; c++)
            {
                var expected = (float)r * 2 + c + 1;
                var actual = BitConverter.ToSingle(subset.Data, (r * 2 + c) * sizeof(float));
                actual.Should().Be(expected, $"cell ({r},{c}) value must be correct after bounded read");
            }
        }
    }

    // ─── BH2-015 regressions ──────────────────────────────────────────────────────

    /// <summary>
    /// Regression test for BH2-015: WriteFillScalar had no case for &lt;u8 (uint64) so
    /// a missing chunk was zero-filled instead of using the declared fill_value.
    /// </summary>
    [Fact]
    public async Task ReadSubsetAsync_UInt64DtypeWithNonzeroFillValue_MissingChunkFillsCorrectly()
    {
        // Build a 2×2 <u8 array with fill_value=42. Omit the chunk so fill path is exercised.
        const ulong fillValue = 42UL;
        var objects = new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["datasets/uint64/.zarray"] = System.Text.Encoding.UTF8.GetBytes(
                """{"chunks":[2,2],"compressor":null,"dtype":"<u8","fill_value":42,"filters":null,"order":"C","shape":[2,2],"zarr_format":2}""")
        };
        var reader = new InMemoryZarrRangeReader(objects);
        var metadata = await new ZarrMetadataExtractor().ReadMetadataAsync(reader, "bucket", "datasets/uint64");

        var request = new ZarrSubsetRequest
        {
            Variable = "uint64",
            Start = new[] { 0, 0 },
            Stop = new[] { 2, 2 }
        };

        var subset = await new ZarrSubsetReader().ReadSubsetAsync(reader, "bucket", "datasets/uint64", metadata, request);

        subset.Shape.Should().Equal(2, 2);
        subset.DataType.Should().Be("<u8");
        subset.Data.Length.Should().Be(2 * 2 * sizeof(ulong));

        for (var i = 0; i < 4; i++)
        {
            var actual = BitConverter.ToUInt64(subset.Data, i * sizeof(ulong));
            actual.Should().Be(fillValue, $"cell {i} should be filled with {fillValue} (BH2-015)");
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps an <see cref="ICloudRangeReader"/> and records every <c>length</c> argument
    /// passed to <see cref="ReadRangeAsync"/> so tests can assert the read ceiling.
    /// </summary>
    private sealed class TrackingZarrRangeReader(ICloudRangeReader inner) : ICloudRangeReader
    {
        private readonly List<int> _lengths = new();

        /// <summary>All <c>length</c> values recorded from calls to <see cref="ReadRangeAsync"/>.</summary>
        public IReadOnlyList<int> RecordedLengths => _lengths;

        public CloudStorageProvider Provider => inner.Provider;

        public Task<byte[]> ReadRangeAsync(
            string bucket, string key, long offset, int length,
            CancellationToken cancellationToken = default)
        {
            _lengths.Add(length);
            return inner.ReadRangeAsync(bucket, key, offset, length, cancellationToken);
        }

        public Task<Stream> ReadRangeStreamAsync(
            string bucket, string key, long offset, int length,
            CancellationToken cancellationToken = default)
            => inner.ReadRangeStreamAsync(bucket, key, offset, length, cancellationToken);

        public Task<long> GetObjectSizeAsync(
            string bucket, string key,
            CancellationToken cancellationToken = default)
            => inner.GetObjectSizeAsync(bucket, key, cancellationToken);
    }
}
