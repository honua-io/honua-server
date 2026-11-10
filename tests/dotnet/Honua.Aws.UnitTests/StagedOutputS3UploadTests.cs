// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Aws.UnitTests;

// Runs the real S3 client against an in-process HTTP endpoint, because the client itself refuses a
// conditional put whose length it cannot determine before any request is sent.
public sealed class StagedOutputS3UploadTests : IAsyncLifetime
{
    private static readonly byte[] _payload = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
    private readonly AcceptingS3Endpoint _endpoint = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _endpoint.DisposeAsync();

    [UnitTest]
    public async Task WriteAsync_FromANonSeekableSource_PutsTheWholeOutputWithAKnownLength()
    {
        var store = new ObjectStorageGeoprocessingOutputObjectStore(
            CreateStorage(),
            Options.Create(new GeoprocessingOutputStagingOptions { Enabled = true }),
            new GeoprocessingOutputStorageKeyPrefix(string.Empty));
        await using var nonSeekable = NonSeekable(_payload);

        var identity = await store.WriteAsync("gp-outputs/job-1/attempt-1/result.ndjson", nonSeekable, "application/x-ndjson");

        Assert.Equal(_payload.Length, identity.SizeBytes);
        var put = Assert.Single(_endpoint.Puts);
        Assert.Equal(_payload.Length, put.DeclaredLength);
        Assert.Equal("*", put.IfNoneMatch);
    }

    [UnitTest]
    public async Task UploadIfMatchAsync_GivenANonSeekableStream_FailsBeforeAnyRequestIsSent()
    {
        await using var nonSeekable = NonSeekable(_payload);

        var upload = await CreateStorage().UploadIfMatchAsync(
            new FileUploadRequest
            {
                Content = nonSeekable,
                FileName = "result.ndjson",
                ContentType = "application/x-ndjson",
                ObjectKeyOverride = "gp-outputs/job-1/attempt-1/direct.ndjson",
                EnableChunkedUpload = false
            },
            expectedETag: null);

        Assert.False(upload.Success);
        Assert.Empty(_endpoint.Puts);
    }

    private AwsS3FileStorage CreateStorage()
        => new(
            Options.Create(new CloudStorageOptions
            {
                Provider = CloudStorageProvider.AwsS3,
                AwsS3 = new AwsS3Options
                {
                    BucketName = "staged-outputs",
                    Region = "us-west-2",
                    AccessKeyId = "test",
                    SecretAccessKey = "test",
                    ServiceUrl = _endpoint.Url,
                    ForcePathStyle = true
                }
            }),
            NullLogger<AwsS3FileStorage>.Instance,
            new DiscardedUploadProgress());

    private static DeflateStream NonSeekable(byte[] bytes)
    {
        var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        compressed.Position = 0;
        return new DeflateStream(compressed, CompressionMode.Decompress);
    }

    private sealed record ReceivedPut(long DeclaredLength, string? IfNoneMatch);

    private sealed class AcceptingS3Endpoint : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public AcceptingS3Endpoint()
        {
            Url = $"http://127.0.0.1:{FreePort()}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _serving = ServeAsync();
        }

        public string Url { get; }

        public ConcurrentQueue<ReceivedPut> Puts { get; } = new();

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serving;
            _listener.Close();
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception stopped) when (stopped is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                await RespondAsync(context);
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var request = context.Request;
            await request.InputStream.CopyToAsync(Stream.Null);
            if (request.HttpMethod == "PUT")
            {
                var declared = request.Headers["x-amz-decoded-content-length"] ?? request.Headers["Content-Length"];
                Puts.Enqueue(new ReceivedPut(long.Parse(declared!, CultureInfo.InvariantCulture), request.Headers["If-None-Match"]));
                context.Response.Headers["ETag"] = "\"staged\"";
                context.Response.StatusCode = (int)HttpStatusCode.OK;
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }

            context.Response.Close();
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
    }

    private sealed class DiscardedUploadProgress : IUploadProgressStore
    {
        public Task SetProgressAsync(string uploadId, UploadProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<UploadProgress?> GetProgressAsync(string uploadId, CancellationToken cancellationToken = default)
            => Task.FromResult<UploadProgress?>(null);

        public Task DeleteProgressAsync(string uploadId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetActiveUploadIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UploadProgress>>([]);
    }
}
