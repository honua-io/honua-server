// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

[Collection(PbfStreamingMemoryTestGroup.Name)]
public sealed partial class PbfQueryFormatterTests
{
    [Fact]
    public async Task FormatAsPbfStream_LargeAttribute_WritesIdenticalBytesInBoundedChunks()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('x', 200_000)));
        var query = QueryResult<Feature>.Create(1, [feature]);
        var (expected, _) = _sut.FormatAsPbf(query, layer, false, null, false, false, null, null, null);
        var (response, contentType) = _sut.FormatAsPbfStream(query, layer, new PbfStreamEncodingOptions
        {
            ReturnGeometry = false,
            OutputSrid = null,
            ReturnZ = false,
            ReturnM = false,
            GeometryPrecision = null,
            MaxAllowableOffset = null,
            OutFields = null
        });
        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await response.ExecuteAsync(context);
        contentType.Should().Be("application/x-protobuf");
        body.LargestWrite.Should().BeLessThanOrEqualTo(64 * 1024);
        body.Length.Should().Be(expected.Length);
        body.Digest().Should().Equal(SHA256.HashData(expected));
    }

    [Fact]
    public async Task FormatAsPbfStream_HundredThousandFeatures_DoesNotRetainAnEncodedPage()
    {
        const int count = 100_000;
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("name", new string('x', 1024));
        var features = Enumerable.Range(1, count).Select(id => Feature.Create(id, null, attributes)).ToImmutableArray();
        var query = QueryResult<Feature>.Create(count, features);
        var baseline = GC.GetTotalMemory(true);
        var (response, _) = _sut.FormatAsPbfStream(query, CreatePointLayer(), new PbfStreamEncodingOptions
        {
            ReturnGeometry = false,
            OutputSrid = null,
            ReturnZ = false,
            ReturnM = false,
            GeometryPrecision = null,
            MaxAllowableOffset = null,
            OutFields = null
        });
        (GC.GetTotalMemory(true) - baseline).Should().BeLessThan(8 * 1024 * 1024);
        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await response.ExecuteAsync(context);
        body.Length.Should().BeGreaterThan(100_000_000);
        body.LargestWrite.Should().BeLessThanOrEqualTo(64 * 1024);
        GC.KeepAlive(query);
    }

    [Fact]
    public async Task FormatAsPbfStream_SmallPayload_StaysInMemoryWithoutScratchFile()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", "small"));
        var query = QueryResult<Feature>.Create(1, [feature]);
        var (expected, _) = _sut.FormatAsPbf(query, layer, false, null, false, false, null, null, null);
        PbfFeatureSpool? capturedSpool = null;
        var (response, contentType) = _sut.FormatAsPbfStream(query, layer, new PbfStreamEncodingOptions
        {
            ReturnGeometry = false
        }, () => capturedSpool = new PbfFeatureSpool());

        capturedSpool.Should().NotBeNull();
        capturedSpool!.UsedScratchFile.Should().BeFalse();

        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await response.ExecuteAsync(context);

        contentType.Should().Be("application/x-protobuf");
        context.Response.ContentLength.Should().Be(expected.Length);
        body.Length.Should().Be(expected.Length);
        body.Digest().Should().Equal(SHA256.HashData(expected));
    }

    [Fact]
    public async Task FormatAsPbfStream_LargePayload_SpillsToScratchAndDeletesOnComplete()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('z', 200_000)));
        var query = QueryResult<Feature>.Create(1, [feature]);
        var (expected, _) = _sut.FormatAsPbf(query, layer, false, null, false, false, null, null, null);
        PbfFeatureSpool? capturedSpool = null;
        var (response, _) = _sut.FormatAsPbfStream(query, layer, new PbfStreamEncodingOptions
        {
            ReturnGeometry = false
        }, () => capturedSpool = new PbfFeatureSpool());

        capturedSpool.Should().NotBeNull();
        capturedSpool!.UsedScratchFile.Should().BeTrue();
        var scratchPath = capturedSpool.ScratchPath;
        scratchPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(scratchPath!).Should().BeTrue();

        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await response.ExecuteAsync(context);

        File.Exists(scratchPath!).Should().BeFalse();
        body.Length.Should().Be(expected.Length);
        body.Digest().Should().Equal(SHA256.HashData(expected));
    }

    [Fact]
    public async Task FormatAsPbfStream_DoesNotRetainMaterializedFeaturesWhileWaitingToDrain()
    {
        var layer = CreatePointLayer();
        var (response, weakFeature) = CreateSpooledResultWithoutKeepingFeatures(layer);

        for (var i = 0; i < 3 && weakFeature.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        weakFeature.IsAlive.Should().BeFalse(
            "the spooled PBF result must not retain the materialized Feature page while waiting for a slow client");

        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await response.ExecuteAsync(context);
        body.Length.Should().BeGreaterThan(200_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (IResult Response, WeakReference WeakFeature) CreateSpooledResultWithoutKeepingFeatures(
        Honua.Core.Features.Metadata.Domain.V2.MetadataV2Object layer)
    {
        var feature = Feature.Create(99, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('a', 200_000)));
        var weak = new WeakReference(feature);
        var query = QueryResult<Feature>.Create(1, [feature]);
        var (response, _) = _sut.FormatAsPbfStream(query, layer, new PbfStreamEncodingOptions
        {
            ReturnGeometry = false
        });
        return (response, weak);
    }

    [Fact]
    public async Task FormatAsPbfStream_CancelledBeforeDrain_DeletesScratchFile()
    {
        var layer = CreatePointLayer();
        var feature = Feature.Create(5, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('c', 200_000)));
        var query = QueryResult<Feature>.Create(1, [feature]);
        PbfFeatureSpool? capturedSpool = null;
        var (response, _) = _sut.FormatAsPbfStream(query, layer, new PbfStreamEncodingOptions
        {
            ReturnGeometry = false
        }, () => capturedSpool = new PbfFeatureSpool());

        var scratchPath = capturedSpool!.ScratchPath!;
        File.Exists(scratchPath).Should().BeTrue();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var context = new DefaultHttpContext
        {
            RequestAborted = cts.Token
        };
        context.Response.Body = new BinaryWriteDigest();

        var act = async () => await response.ExecuteAsync(context);
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(scratchPath).Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsPbfAsync_CancelledDuringScratchWrite_DeletesScratchFile()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('c', 200_000)));
        PbfFeatureSpool? spool = null;
        string? scratchPath = null;

        using var cts = new CancellationTokenSource();
        using var body = new DisconnectOnSpoolChunkStream(() =>
        {
            scratchPath = spool!.ScratchPath;
            scratchPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(scratchPath).Should().BeTrue();
            cts.Cancel();
        });
        var context = new DefaultHttpContext { RequestAborted = cts.Token };
        context.Response.Body = body;

        var act = async () => await _sut.StreamAsPbfAsync(
            new PbfStreamSource
            {
                Features = ToAsyncEnumerable([feature]),
                HasMoreResults = static () => false,
                Resource = CreateLayerWithStringField(),
                Options = new PbfStreamEncodingOptions { ReturnGeometry = false },
                SpoolFactory = () => spool = new PbfFeatureSpool()
            },
            context,
            context.RequestAborted);

        await act.Should().ThrowAsync<OperationCanceledException>()
            .Where(ex => ex.CancellationToken == cts.Token);
        body.Length.Should().BePositive();
        File.Exists(scratchPath).Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsPbfAsync_BlockedScratchWrite_ResumesWithIdenticalBytes()
    {
        var layer = CreateLayerWithStringField();
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('b', 4 * PbfFeatureSpool.StreamChunkBytes)));
        var query = QueryResult<Feature>.Create(1, [feature]);
        var (expected, _) = _sut.FormatAsPbf(query, layer, false, null, false, false, null, null, null);
        using var body = new PauseOnSpoolChunkStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        var response = _sut.StreamAsPbfAsync(
            new PbfStreamSource
            {
                Features = ToAsyncEnumerable([feature]),
                HasMoreResults = static () => false,
                Resource = layer,
                Options = new PbfStreamEncodingOptions { ReturnGeometry = false }
            },
            context,
            CancellationToken.None);

        try
        {
            await body.FirstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            response.IsCompleted.Should().BeFalse();
        }
        finally
        {
            body.Resume.TrySetResult();
        }

        await response;
        context.Response.ContentLength.Should().Be(expected.Length);
        body.ToArray().Should().Equal(expected);
    }

    [Fact]
    public Task StreamAsPbfAsync_WithNoMoreResults_MatchesFormatAsPbfByteForByte()
    {
        var items = CreateParityItems();
        return AssertStreamAsPbfMatchesFormatAsPbfAsync(QueryResult<Feature>.Create(items.Length, items, hasMoreResults: false));
    }

    [Fact]
    public Task StreamAsPbfAsync_WithMoreResults_MatchesFormatAsPbfByteForByte()
    {
        var items = CreateParityItems();
        return AssertStreamAsPbfMatchesFormatAsPbfAsync(QueryResult<Feature>.Create(items.Length, items, hasMoreResults: true));
    }

    private static ImmutableArray<Feature> CreateParityItems()
        =>
        [
            Feature.Create(1, CreatePointWkb(-122.4194, 37.7749), ImmutableDictionary<string, object?>.Empty.Add("name", "SF")),
            Feature.Create(2, CreatePointWkb(-157.8583, 21.3069), ImmutableDictionary<string, object?>.Empty.Add("name", "HNL"))
        ];

    private async Task AssertStreamAsPbfMatchesFormatAsPbfAsync(QueryResult<Feature> query)
    {
        var layer = CreateLayerWithStringField();
        var (expected, _) = _sut.FormatAsPbf(query, layer, true, 4326, false, false, null, null, ["name"]);

        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await _sut.StreamAsPbfAsync(
            new PbfStreamSource
            {
                Features = ToAsyncEnumerable(query.Items),
                HasMoreResults = () => query.HasMoreResults,
                Resource = layer,
                Options = new PbfStreamEncodingOptions
                {
                    ReturnGeometry = true,
                    OutputSrid = 4326,
                    ReturnZ = false,
                    ReturnM = false,
                    OutFields = ["name"]
                }
            },
            context,
            CancellationToken.None);

        context.Response.ContentType.Should().Be("application/x-protobuf");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.ContentLength.Should().Be(expected.Length);
        body.Length.Should().Be(expected.Length);
        body.Digest().Should().Equal(SHA256.HashData(expected));
    }

    [Fact]
    public async Task StreamAsPbfAsync_HundredThousandFreshFeatures_KeepsManagedHeapBoundedAndDeletesScratch()
    {
        const int count = 100_000;
        var layer = CreateLayerWithStringField();
        PbfFeatureSpool? capturedSpool = null;

        var baseline = GC.GetTotalMemory(true);
        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await _sut.StreamAsPbfAsync(
            new PbfStreamSource
            {
                Features = GenerateFreshFeaturesAsync(count),
                HasMoreResults = static () => true,
                Resource = layer,
                Options = new PbfStreamEncodingOptions
                {
                    ReturnGeometry = false
                },
                SpoolFactory = () =>
                {
                    capturedSpool = new PbfFeatureSpool();
                    return capturedSpool;
                }
            },
            context,
            CancellationToken.None);

        (GC.GetTotalMemory(true) - baseline).Should().BeLessThan(8 * 1024 * 1024);
        capturedSpool.Should().NotBeNull();
        capturedSpool!.UsedScratchFile.Should().BeTrue();
        var scratchPath = capturedSpool.ScratchPath;
        scratchPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(scratchPath!).Should().BeFalse();
        body.Length.Should().BeGreaterThan(100_000_000);
        body.LargestWrite.Should().BeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public async Task StreamAsPbfAsync_SourceFailsAfterSpill_DeletesScratchFileWithZeroBytesWritten()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('x', 200_000)));
        var sourceFailure = new IOException("Feature source interrupted.");
        PbfFeatureSpool? spool = null;
        string? scratchPath = null;

        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        var act = async () => await _sut.StreamAsPbfAsync(
            new PbfStreamSource
            {
                Features = ReadFeaturesAsync(),
                HasMoreResults = static () => false,
                Resource = CreateLayerWithStringField(),
                Options = new PbfStreamEncodingOptions { ReturnGeometry = false },
                SpoolFactory = () => spool = new PbfFeatureSpool()
            },
            context,
            CancellationToken.None);

        var failure = await act.Should().ThrowAsync<IOException>();
        failure.Which.Should().BeSameAs(sourceFailure);
        body.Length.Should().Be(0);
        File.Exists(scratchPath).Should().BeFalse();

        async IAsyncEnumerable<Feature> ReadFeaturesAsync()
        {
            await Task.CompletedTask;
            yield return feature;
            scratchPath = spool!.ScratchPath;
            scratchPath.Should().NotBeNullOrWhiteSpace();
            File.Exists(scratchPath).Should().BeTrue();
            throw sourceFailure;
        }
    }

    [Fact]
    public async Task StreamAsPbfAsync_SourceCancelledAfterSpill_ReleasesSourceAndScratchFile()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", new string('x', 200_000)));
        PbfFeatureSpool? spool = null;
        string? scratchPath = null;
        var sourceReleased = false;

        using var cts = new CancellationTokenSource();
        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext { RequestAborted = cts.Token };
        context.Response.Body = body;

        var act = async () => await _sut.StreamAsPbfAsync(
            new PbfStreamSource
            {
                Features = ReadFeaturesAsync(),
                HasMoreResults = static () => false,
                Resource = CreateLayerWithStringField(),
                Options = new PbfStreamEncodingOptions { ReturnGeometry = false },
                SpoolFactory = () => spool = new PbfFeatureSpool()
            },
            context,
            context.RequestAborted);

        await act.Should().ThrowAsync<OperationCanceledException>()
            .Where(ex => ex.CancellationToken == cts.Token);
        sourceReleased.Should().BeTrue();
        body.Length.Should().Be(0);
        File.Exists(scratchPath).Should().BeFalse();

        async IAsyncEnumerable<Feature> ReadFeaturesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return feature;
                scratchPath = spool!.ScratchPath;
                scratchPath.Should().NotBeNullOrWhiteSpace();
                File.Exists(scratchPath).Should().BeTrue();
                cancellationToken.Should().Be(cts.Token);
                await cts.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                sourceReleased = true;
            }
        }
    }

    [Fact]
    public async Task StreamAsPbfAsync_ScratchFullOnFinalFlush_ThrowsServiceUnavailableWithZeroBytesWritten()
    {
        var layer = CreateLayerWithStringField();
        var items = ImmutableArray.Create(
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("name", new string('f', 128))));

        using var body = new BinaryWriteDigest();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        var act = async () => await _sut.StreamAsPbfAsync(
            new PbfStreamSource
            {
                Features = ToAsyncEnumerable(items),
                HasMoreResults = static () => false,
                Resource = layer,
                Options = new PbfStreamEncodingOptions { ReturnGeometry = false },
                SpoolFactory = () => new PbfFeatureSpool(16, () => new FailingFlushStream(28))
            },
            context,
            CancellationToken.None);

        await act.Should().ThrowAsync<ServiceUnavailableException>()
            .Where(ex => ex.RetryAfterSeconds == PbfFeatureSpool.ScratchFullRetryAfterSeconds);
        body.Length.Should().Be(0);
    }

    [Theory]
    [InlineData(28)]  // ENOSPC
    [InlineData(122)] // EDQUOT
    public void PbfFeatureSpool_ScratchFullOnCreateOrWrite_ThrowsServiceUnavailableWithRetryAfter(int hresult)
    {
        using var createSpool = new PbfFeatureSpool(16, () => throw new IOException("No space left on device", hresult));
        var createAct = () => createSpool.Write(new byte[32]);
        createAct.Should().Throw<ServiceUnavailableException>()
            .Where(ex => ex.RetryAfterSeconds == PbfFeatureSpool.ScratchFullRetryAfterSeconds);

        using var writeSpool = new PbfFeatureSpool(16, () => new FailingWriteStream(hresult));
        var writeAct = () => writeSpool.Write(new byte[32]);
        writeAct.Should().Throw<ServiceUnavailableException>()
            .Where(ex => ex.RetryAfterSeconds == PbfFeatureSpool.ScratchFullRetryAfterSeconds);
    }

    private static async IAsyncEnumerable<Feature> ToAsyncEnumerable(
        ImmutableArray<Feature> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Feature> GenerateFreshFeaturesAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = new string('x', 1024);
        for (var id = 1; id <= count; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty.Add("name", payload));
        }

        await Task.CompletedTask;
    }

    private sealed class FailingWriteStream(int hresult) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() => throw new IOException("No space left on device", hresult);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("No space left on device", hresult);
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("No space left on device", hresult);
    }

    private sealed class FailingFlushStream(int hresult) : MemoryStream
    {
        public override void Flush() => throw new IOException("No space left on device", hresult);
    }

    private sealed class DisconnectOnSpoolChunkStream(Action disconnect) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == PbfFeatureSpool.StreamChunkBytes)
            {
                disconnect();
                return ValueTask.FromCanceled(cancellationToken);
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class PauseOnSpoolChunkStream : MemoryStream
    {
        public TaskCompletionSource FirstChunkStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _firstChunkWritten;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FirstChunkStarted.Task.IsCompleted)
            {
                _firstChunkWritten.Should().BeTrue("the scratch drain must await each response write");
            }
            else if (buffer.Length == PbfFeatureSpool.StreamChunkBytes)
            {
                FirstChunkStarted.SetResult();
                await Resume.Task.WaitAsync(cancellationToken);
                await base.WriteAsync(buffer, cancellationToken);
                _firstChunkWritten = true;
                return;
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BinaryWriteDigest : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        public int LargestWrite { get; private set; }
        public override long Length => _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public byte[] Digest() => _hash.GetHashAndReset();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            _length += buffer.Length;
            _hash.AppendData(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

// Process-wide heap measurements must not overlap allocations from other test collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PbfStreamingMemoryTestGroup
{
    public const string Name = "PBF streaming memory";
}
