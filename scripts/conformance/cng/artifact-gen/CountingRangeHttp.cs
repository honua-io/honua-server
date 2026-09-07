// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

// A range-capable local HTTP origin and an ICloudRangeReader over it.
//
// #4398: the cloud-native lane's COG and Zarr cells validated artifacts that
// `rio_cogeo.cog_translate` and `xarray.to_zarr` wrote, so no Honua code was in
// the loop, and the declared `min_range_requests` / `max_full_object_downloads`
// budgets had nothing measuring them. Both gaps have the same root cause: the
// lane never exercised Honua's own cloud-native consumer path.
//
// This file closes it. The origin below is a real HTTP/1.1 server that honours
// `Range` and records, per object key, how many requests Honua issued, how many
// carried a byte range, how many pulled a whole object, and how many bytes
// crossed the wire. Honua's `CogMetadataExtractor` / `ZarrSubsetReader` then read
// through `HttpRangeCloudReader`, so the artifacts the lane validates downstream
// are ones Honua transcoded, and the range-efficiency budgets are measured
// against observed HTTP traffic rather than assumed.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Cng.ArtifactGen;

/// <summary>Observed HTTP traffic for one Honua cloud-native read.</summary>
internal sealed class TransferCounters
{
    private int _requests;
    private int _rangeRequests;
    private int _fullObjectDownloads;
    private long _transferredBytes;
    private readonly ConcurrentDictionary<string, int> _requestsByKey = new(StringComparer.Ordinal);

    public int Requests => Volatile.Read(ref _requests);

    public int RangeRequests => Volatile.Read(ref _rangeRequests);

    public int FullObjectDownloads => Volatile.Read(ref _fullObjectDownloads);

    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    public void Record(string key, bool ranged, bool wholeObject, long bytes)
    {
        Interlocked.Increment(ref _requests);
        if (ranged && !wholeObject)
        {
            Interlocked.Increment(ref _rangeRequests);
        }

        if (wholeObject)
        {
            Interlocked.Increment(ref _fullObjectDownloads);
        }

        Interlocked.Add(ref _transferredBytes, bytes);
        _requestsByKey.AddOrUpdate(key, 1, (_, existing) => existing + 1);
    }

    /// <summary>
    /// Counts the distinct chunk objects (everything that is not a store metadata
    /// document) touched under a Zarr variable prefix. Chunk pruning is the
    /// range-efficiency property of a Zarr subset read: touching fewer chunks than the
    /// array holds is what makes the access cloud-native, and the count is checkable
    /// against the chunk grid independently of byte totals. Distinct objects, not
    /// requests, because one chunk costs an identity request plus a payload request.
    /// </summary>
    public int DistinctChunkObjects(string variablePrefix)
        => _requestsByKey.Keys
            .Count(key => key.StartsWith(variablePrefix, StringComparison.Ordinal)
                && !Path.GetFileName(key).StartsWith('.')
                && !string.Equals(Path.GetFileName(key), "zarr.json", StringComparison.Ordinal));

    public Dictionary<string, object> ToEvidence() => new(StringComparer.Ordinal)
    {
        ["requests"] = Requests,
        ["range_requests"] = RangeRequests,
        ["full_object_downloads"] = FullObjectDownloads,
        ["transferred_bytes"] = TransferredBytes,
        ["distinct_objects"] = _requestsByKey.Count,
    };
}

/// <summary>
/// Minimal range-capable HTTP/1.1 origin over a directory. Deliberately raw TCP so
/// nothing between Honua's reader and the byte counters can buffer, pool or
/// transparently retry the reads being measured.
/// </summary>
internal sealed class LocalRangeHttpOrigin : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly DirectoryInfo _root;
    private readonly TransferCounters _counters;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    public LocalRangeHttpOrigin(string rootDirectory, TransferCounters counters)
    {
        _root = new DirectoryInfo(Path.GetFullPath(rootDirectory));
        _counters = counters;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                    if (request is null)
                    {
                        return;
                    }

                    await RespondAsync(stream, request.Value.Method, request.Value.Target, request.Value.Range)
                        .ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // A client that hangs up mid-response is not a lane failure.
                }
            }
        }
    }

    private static async Task<(string Method, string Target, string? Range)?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            buffer.Add(single[0]);
            var count = buffer.Count;
            if (count >= 4 && buffer[count - 4] == (byte)'\r' && buffer[count - 3] == (byte)'\n'
                && buffer[count - 2] == (byte)'\r' && buffer[count - 1] == (byte)'\n')
            {
                break;
            }

            if (count > 64 * 1024)
            {
                return null;
            }
        }

        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }

        var range = lines.Skip(1)
            .FirstOrDefault(line => line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2)[1].Trim();
        return (requestLine[0].ToUpperInvariant(), requestLine[1], range);
    }

    private async Task RespondAsync(NetworkStream stream, string method, string target, string? range)
    {
        var relative = Uri.UnescapeDataString(target.Split('?')[0]).TrimStart('/');
        var path = Path.GetFullPath(Path.Combine(_root.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root.FullName, StringComparison.Ordinal) || !File.Exists(path))
        {
            await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                .ConfigureAwait(false);
            return;
        }

        var content = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(content))[..32]}\"";
        if (method == "HEAD")
        {
            // A HEAD carries no payload, so it is a request but neither a range read
            // nor a full-object download.
            _counters.Record(relative, ranged: false, wholeObject: false, bytes: 0);
            await WriteAsync(
                stream,
                $"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nAccept-Ranges: bytes\r\nETag: {etag}\r\nConnection: close\r\n\r\n")
                .ConfigureAwait(false);
            return;
        }

        long start = 0;
        var end = content.LongLength - 1;
        var ranged = false;
        if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range["bytes=".Length..].Split('-', 2);
            if (spec.Length == 2
                && long.TryParse(spec[0], NumberStyles.None, CultureInfo.InvariantCulture, out var requestedStart))
            {
                ranged = true;
                start = requestedStart;
                if (long.TryParse(spec[1], NumberStyles.None, CultureInfo.InvariantCulture, out var requestedEnd))
                {
                    end = Math.Min(requestedEnd, content.LongLength - 1);
                }
            }
        }

        if (start >= content.LongLength || start > end)
        {
            _counters.Record(relative, ranged: true, wholeObject: false, bytes: 0);
            await WriteAsync(
                stream,
                $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{content.LongLength}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                .ConfigureAwait(false);
            return;
        }

        var length = (int)(end - start + 1);
        // A range that spans the entire object still transfers the whole object; only
        // the header spelling differs, so it must not be credited as a partial read.
        var wholeObject = length == content.Length;
        _counters.Record(relative, ranged, wholeObject, length);

        var statusLine = ranged
            ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{content.LongLength}\r\n"
            : "HTTP/1.1 200 OK\r\n";
        await WriteAsync(
            stream,
            $"{statusLine}Content-Length: {length}\r\nAccept-Ranges: bytes\r\nETag: {etag}\r\nConnection: close\r\n\r\n")
            .ConfigureAwait(false);
        await stream.WriteAsync(content.AsMemory((int)start, length)).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static Task WriteAsync(NetworkStream stream, string text)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }
}

/// <summary>
/// <see cref="ICloudRangeReader"/> backed by HTTP range requests against
/// <see cref="LocalRangeHttpOrigin"/>. Honua's production COG and Zarr readers take
/// this interface, so driving them through it exercises the real cloud-native read
/// path end to end instead of a local file handle.
/// </summary>
internal sealed class HttpRangeCloudReader(HttpClient client) : ICloudRangeReader
{
    public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

    public async Task<byte[]> ReadRangeAsync(
        string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, key);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return [];
        }

        ThrowIfMissing(response, key);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> ReadRangeAsync(
        string bucket,
        string key,
        long offset,
        int length,
        string expectedETag,
        CancellationToken cancellationToken = default)
        => ReadRangeAsync(bucket, key, offset, length, cancellationToken);

    public async Task<Stream> ReadRangeStreamAsync(
        string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        => new MemoryStream(await ReadRangeAsync(bucket, key, offset, length, cancellationToken).ConfigureAwait(false));

    public async Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => (await GetObjectMetadataAsync(bucket, key, cancellationToken).ConfigureAwait(false)).SizeBytes;

    public async Task<CloudObjectMetadata> GetObjectMetadataAsync(
        string bucket, string key, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, key);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        ThrowIfMissing(response, key);
        response.EnsureSuccessStatusCode();
        return new CloudObjectMetadata
        {
            SizeBytes = response.Content.Headers.ContentLength ?? 0,
            ETag = response.Headers.ETag?.Tag,
        };
    }

    /// <summary>
    /// Surfaces a missing object the way the object-store readers do. Honua's Zarr
    /// metadata probe distinguishes "this store has no <c>zarr.json</c>, so it is v2"
    /// from a transport failure by catching <see cref="FileNotFoundException"/>; a raw
    /// 404 would abort the read instead of falling back.
    /// </summary>
    private static void ThrowIfMissing(HttpResponseMessage response, string key)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object '{key}' does not exist in the conformance origin.", key);
        }
    }
}
