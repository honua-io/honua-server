// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

// A GeoParquet response spools to scratch storage; a full file system must reach the
// client as a retryable 503, not as a server error.
public sealed class GeoParquetScratchFailureTests
{
    private const int NoSpaceLeftOnDevice = 28;

    [UnitTest]
    public void MapWriteFailure_WithWrappedStorageFullError_ReturnsRetryableServiceUnavailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var stream = new GeoParquetFeatureWriter.BoundedParquetStream(1024);
        var failure = new InvalidOperationException("native write failed", new IOException("full", NoSpaceLeftOnDevice));

        var mapped = GeoParquetFeatureWriter.MapWriteFailure(failure, stream);

        mapped.Should().BeOfType<ServiceUnavailableException>()
            .Which.RetryAfterSeconds.Should().Be(GeoParquetFeatureWriter.ScratchStorageFullRetryAfterSeconds);
    }

    [UnitTest]
    public void MapWriteFailure_WhenTheBudgetWasExceeded_KeepsThePayloadTooLargeRefusal()
    {
        using var stream = new GeoParquetFeatureWriter.BoundedParquetStream(4);
        var refusal = FluentActions.Invoking(() => stream.Write(new byte[8])).Should()
            .Throw<GeoParquetLimitExceededException>().Which;

        using var untouched = new GeoParquetFeatureWriter.BoundedParquetStream(4);

        GeoParquetFeatureWriter.MapWriteFailure(refusal, stream).Should().BeOfType<GeoParquetLimitExceededException>();
        GeoParquetFeatureWriter.MapWriteFailure(new IOException("other"), untouched).Should().BeNull();
    }

    [UnitTest]
    public void DisposeAfterFailedWrite_WithBufferedBytesOnAFullDevice_DoesNotReplaceTheFailure()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/full"))
        {
            return;
        }

        // Buffered bytes that cannot be written: a plain Dispose repeats ENOSPC.
        // Both are disposed in the finally below; a second plain Dispose would rethrow ENOSPC.
#pragma warning disable CA2000
        var plain = OpenFullDeviceWithBufferedBytes();
        var guarded = OpenFullDeviceWithBufferedBytes();
#pragma warning restore CA2000
        try
        {
            FluentActions.Invoking(plain.Dispose).Should().Throw<IOException>();
            FluentActions.Invoking(() => GeoParquetFeatureWriter.DisposeAfterFailedWrite(guarded)).Should().NotThrow();
            FluentActions.Invoking(() => guarded.WriteByte(1)).Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            GeoParquetFeatureWriter.DisposeAfterFailedWrite(plain);
            GeoParquetFeatureWriter.DisposeAfterFailedWrite(guarded);
        }
    }

    private static FileStream OpenFullDeviceWithBufferedBytes()
    {
        var stream = new FileStream("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 64 * 1024);
        stream.Write(new byte[16]);
        FluentActions.Invoking(stream.Flush).Should().Throw<IOException>();
        return stream;
    }
}
