// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

/// <summary>
/// Regression tests for the AWS SDK v4 null-collection behavior (honua-server#2311): when a
/// ListObjectsV2 call matches zero objects, S3 returns a ListBucketResult with no
/// &lt;Contents&gt; elements and the SDK v4 unmarshaller leaves <c>response.S3Objects</c>
/// <c>null</c> instead of an empty list. Unguarded dereferences of that collection threw
/// <c>NullReferenceException</c> from every listing over an empty prefix — most visibly
/// <c>CloudBackedTemporaryFileService.EnsureCloudStorageCapacityAsync</c>'s quota listing of the
/// empty <c>temporary-files/</c> prefix, which turned every rendered map-image
/// <c>href</c>/<c>f=json</c> export (MapServer <c>export</c>, ImageServer <c>exportImage</c>)
/// into a 500 on S3-backed deployments such as demo.honua.io.
///
/// The tests drive the real <see cref="AwsS3FileStorage"/> and the real SDK serialization
/// pipeline against a local HTTP stub that answers ListObjectsV2 with the exact empty
/// ListBucketResult payload live S3 returns for an empty prefix, so the S3Objects-null shape is
/// produced by the SDK itself rather than hand-constructed.
/// </summary>
[Collection("Unit")]
public sealed class AwsS3FileStorageEmptyListingTests
{
    private const string EmptyListBucketResultXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
            <Name>unit-test-bucket</Name>
            <Prefix>temporary-files/</Prefix>
            <KeyCount>0</KeyCount>
            <MaxKeys>1000</MaxKeys>
            <IsTruncated>false</IsTruncated>
        </ListBucketResult>
        """;

    [UnitTest]
    public async Task ListFilesAsync_EmptyPrefix_ReturnsEmptyListInsteadOfThrowing()
    {
        using var stub = EmptyListObjectsStub.Start();
        var storage = CreateStorage(stub.ServiceUrl);

        var files = await storage.ListFilesAsync(
            "temporary-files",
            maxResults: int.MaxValue,
            includeMetadata: true);

        files.Should().BeEmpty(
            "an empty S3 prefix (SDK v4 unmarshals it as S3Objects == null) must produce an empty " +
            "listing, not a NullReferenceException — the NRE 500'd every rendered map-image " +
            "href/f=json export on S3-backed deployments (honua-server#2311)");
        stub.ListRequestCount.Should().BeGreaterThan(0, "the storage provider must have issued a ListObjectsV2 request");
    }

    [UnitTest]
    public async Task CleanupExpiredFilesAsync_EmptyBucket_ReturnsZeroInsteadOfThrowing()
    {
        using var stub = EmptyListObjectsStub.Start();
        var storage = CreateStorage(stub.ServiceUrl);

        var cleaned = await storage.CleanupExpiredFilesAsync();

        cleaned.Should().Be(0);
    }

    [UnitTest]
    public async Task DeleteBatchAsync_EmptyPrefix_ReturnsZeroInsteadOfThrowing()
    {
        using var stub = EmptyListObjectsStub.Start();
        var storage = CreateStorage(stub.ServiceUrl);

        var deleted = await storage.DeleteBatchAsync("no-such-batch");

        deleted.Should().Be(0);
    }

    private static AwsS3FileStorage CreateStorage(string serviceUrl)
    {
        var options = Options.Create(new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "unit-test-bucket",
                Region = "us-east-1",
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                ServiceUrl = serviceUrl,
                ForcePathStyle = true
            }
        });

        return new AwsS3FileStorage(
            options,
            NullLogger<AwsS3FileStorage>.Instance,
            new InMemoryUploadProgressStore());
    }

    /// <summary>
    /// Minimal local S3 endpoint that answers every GET (ListObjectsV2) with the canonical
    /// empty ListBucketResult document. Requests other than listings are answered 404 so any
    /// unexpected follow-up call (e.g. a per-object HEAD) surfaces as a test failure rather
    /// than hanging.
    /// </summary>
    private sealed class EmptyListObjectsStub : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveLoop;
        private int _listRequestCount;

        public string ServiceUrl { get; }

        public int ListRequestCount => Volatile.Read(ref _listRequestCount);

        private EmptyListObjectsStub(HttpListener listener, string serviceUrl)
        {
            _listener = listener;
            ServiceUrl = serviceUrl;
            _serveLoop = Task.Run(ServeAsync);
        }

        public static EmptyListObjectsStub Start()
        {
            // HttpListener cannot bind port 0, so probe for a free port. Retry a few times to
            // absorb the small race between picking the port and binding the listener.
            for (var attempt = 0; ; attempt++)
            {
                var port = GetFreeTcpPort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return new EmptyListObjectsStub(listener, $"http://127.0.0.1:{port}");
                }
                catch (HttpListenerException) when (attempt < 5)
                {
                    listener.Close();
                }
            }
        }

        private static int GetFreeTcpPort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_shutdown.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                try
                {
                    if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal) &&
                        context.Request.QueryString["list-type"] == "2")
                    {
                        Interlocked.Increment(ref _listRequestCount);
                        var payload = Encoding.UTF8.GetBytes(EmptyListBucketResultXml);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/xml";
                        context.Response.ContentLength64 = payload.Length;
                        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                    }
                }
                catch (Exception)
                {
                    // The client may abort mid-response during shutdown; ignore.
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        // Already closed/aborted.
                    }
                }
            }
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // Listener may already be stopped.
            }

            try
            {
                _serveLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Best-effort shutdown; the loop exits on listener stop.
            }

            _shutdown.Dispose();
        }
    }
}
