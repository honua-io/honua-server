// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Apache.Arrow;
using Apache.Arrow.Types;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

// A GeoParquet response spools to scratch storage. When that volume is full the
// request must get a retryable 503 with Retry-After, not a generic query failure.
public sealed class GeoParquetScratchFullTests
{
    private const int NoSpaceLeftOnDevice = 28;

    [UnitTest]
    public void WriteArrowParquet_ScratchVolumeFills_ThrowsRetryableServiceUnavailableAndReleasesSpool()
    {
        using var spool = new FullAfterStream(capacityBytes: 4 * 1024);
        var schema = new Schema.Builder().Field(field => field.Name("payload").DataType(StringType.Default)).Build();

        var act = () => GeoParquetFeatureWriter.WriteArrowParquet(schema, writer =>
        {
            var values = new StringArray.Builder();
            for (var row = 0; row < 64; row++)
            {
                values.Append(new string((char)('a' + row % 26), 4096));
            }

            using var batch = new RecordBatch(schema, [values.Build()], 64);
            writer.WriteRecordBatch(batch);
        }, new GeoParquetFeatureWriter.BoundedParquetStream(int.MaxValue, spool));

        var refusal = act.Should().Throw<ServiceUnavailableException>().Which;
        refusal.RetryAfterSeconds.Should().Be(GeoParquetFeatureWriter.ScratchFullRetryAfterSeconds);
        refusal.Message.Should().Be(GeoParquetFeatureWriter.ScratchFullClientMessage);
        spool.Disposed.Should().BeTrue("a refused spool must not keep its scratch space");
    }

    [UnitTest]
    public void WriteArrowParquet_ResponseBudgetExceededBeforeScratchFills_StillRefusesAsPayloadTooLarge()
    {
        using var spool = new FullAfterStream(capacityBytes: 1024 * 1024);
        var schema = new Schema.Builder().Field(field => field.Name("payload").DataType(StringType.Default)).Build();

        var act = () => GeoParquetFeatureWriter.WriteArrowParquet(schema, writeBatches: null,
            new GeoParquetFeatureWriter.BoundedParquetStream(maxBytes: 8, spool));

        act.Should().Throw<GeoParquetLimitExceededException>();
        spool.Disposed.Should().BeTrue();
    }

    [UnitTest]
    public void IsStorageFull_RecognizesDiskFullErrorsOnly()
    {
        GeoParquetFeatureWriter.IsStorageFull(new IOException("No space left on device", NoSpaceLeftOnDevice)).Should().BeTrue();
        GeoParquetFeatureWriter.IsStorageFull(new IOException("Disk quota exceeded", 122)).Should().BeTrue();
        GeoParquetFeatureWriter.IsStorageFull(new IOException("disk full", unchecked((int)0x80070070))).Should().BeTrue();
        GeoParquetFeatureWriter.IsStorageFull(
            new InvalidOperationException("native writer failed", new IOException("No space left on device", NoSpaceLeftOnDevice)))
            .Should().BeTrue("the native encoder can wrap the managed write failure");

        GeoParquetFeatureWriter.IsStorageFull(new IOException("Input/output error", 5)).Should().BeFalse();
        GeoParquetFeatureWriter.IsStorageFull(new InvalidOperationException("other", new InvalidDataException("x"))).Should().BeFalse();
        GeoParquetFeatureWriter.IsStorageFull(null).Should().BeFalse();
    }

    // A seekable spool that refuses writes past a fixed capacity the way a full tmpfs does.
    // Like a buffered FileStream, it retries the refused bytes on close and fails again.
    private sealed class FullAfterStream(int capacityBytes) : MemoryStream
    {
        private bool refused;

        public bool Disposed { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Refuse(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Refuse(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Refuse(1);
            base.WriteByte(value);
        }

        private void Refuse(int count)
        {
            if (Position + count > capacityBytes)
            {
                refused = true;
                throw new IOException("No space left on device : '/tmp/honua/honua-parquet-test'", NoSpaceLeftOnDevice);
            }
        }

        // Only the first close fails, as the runtime's buffered retry does.
        public override void Close()
        {
            var failClose = refused && !Disposed;
            Disposed = true;
            base.Close();
            if (failClose)
            {
                throw new IOException("No space left on device : '/tmp/honua/honua-parquet-test'", NoSpaceLeftOnDevice);
            }
        }
    }
}
