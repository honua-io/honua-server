// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;

namespace Honua.Server.Tests.Features.Protocols.Cog;

/// <summary>
/// A transparent reverse proxy in front of the S3 emulator that records the HTTP conversation.
/// <para>
/// The server builds its own <c>AwsS3RangeReader</c> from <c>FileStorage:AwsS3</c>
/// configuration, so there is no seam to substitute without leaving the production read path.
/// Pointing <c>ServiceUrl</c> at this proxy keeps that path intact and still makes the wire
/// observable: which ranges were asked for, what status came back, what <c>Content-Range</c>
/// was served, and how many bytes actually crossed the boundary.
/// </para>
/// </summary>
internal sealed class RecordingRangeProxy : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentQueue<ProxyExchange> _exchanges = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Uri _upstream;
    private readonly Task _pump;

    private RecordingRangeProxy(Uri upstream, int port)
    {
        _upstream = upstream;
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The URL callers should use as the S3 service endpoint.</summary>
    public string BaseUrl { get; }

    public IReadOnlyList<ProxyExchange> Exchanges => [.. _exchanges];

    public static RecordingRangeProxy Start(Uri upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        return new RecordingRangeProxy(upstream, GetFreePort());
    }

    /// <summary>Drops everything recorded so far, so one request can be measured on its own.</summary>
    public void Reset() => _exchanges.Clear();

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Close();

        try
        {
            await _pump;
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
            // Shutdown races the accept loop; the listener is already closed.
        }

        _client.Dispose();
        _shutdown.Dispose();
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private async Task PumpAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_shutdown.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => ForwardAsync(context));
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        try
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(context.Request.HttpMethod),
                new Uri(_upstream, context.Request.RawUrl ?? "/"));

            byte[]? body = null;
            if (context.Request.HasEntityBody)
            {
                using var buffer = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(buffer);
                body = buffer.ToArray();
                request.Content = new ByteArrayContent(body);
            }

            foreach (var name in context.Request.Headers.AllKeys)
            {
                if (name is null || IsHopByHop(name))
                {
                    continue;
                }

                var value = context.Request.Headers[name];
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            var payload = await response.Content.ReadAsByteArrayAsync();

            var contentRange = response.Content.Headers.ContentRange;
            _exchanges.Enqueue(new ProxyExchange(
                context.Request.HttpMethod,
                context.Request.RawUrl ?? string.Empty,
                context.Request.Headers["Range"],
                (int)response.StatusCode,
                contentRange?.From,
                contentRange?.To,
                contentRange?.Length,
                payload.LongLength));

            context.Response.StatusCode = (int)response.StatusCode;
            CopyHeaders(response.Headers, context.Response);
            CopyHeaders(response.Content.Headers, context.Response);
            context.Response.ContentLength64 = payload.LongLength;
            await context.Response.OutputStream.WriteAsync(payload);
        }
        catch (Exception)
        {
            // A proxy failure must look like an upstream failure, never a hang: the test that
            // asserts a bounded error would otherwise stall on the socket.
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
            }
            catch (Exception)
            {
                // Response already committed.
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (Exception)
            {
                // Client disconnected.
            }
        }
    }

    private static void CopyHeaders(
        System.Net.Http.Headers.HttpHeaders source,
        HttpListenerResponse destination)
    {
        foreach (var header in source)
        {
            if (IsHopByHop(header.Key) || IsListenerManaged(header.Key))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                try
                {
                    destination.Headers.Add(header.Key, value);
                }
                catch (ArgumentException)
                {
                    // HttpListener refuses a few restricted headers; they are not part of the
                    // evidence this proxy exists to collect.
                }
            }
        }
    }

    private static bool IsHopByHop(string name)
        => name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Expect", StringComparison.OrdinalIgnoreCase);

    private static bool IsListenerManaged(string name)
        => name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Date", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>One request/response pair observed on the object-store boundary.</summary>
    /// <param name="Method">HTTP method.</param>
    /// <param name="Path">Raw request URL, including the bucket and key.</param>
    /// <param name="RequestRange">The <c>Range</c> request header, or null when absent.</param>
    /// <param name="StatusCode">Upstream status code.</param>
    /// <param name="ContentRangeFrom">First byte of the served range, from <c>Content-Range</c>.</param>
    /// <param name="ContentRangeTo">Last byte of the served range, from <c>Content-Range</c>.</param>
    /// <param name="ContentRangeLength">Total object length, from <c>Content-Range</c>.</param>
    /// <param name="ResponseBytes">Bytes in the response body.</param>
    internal sealed record ProxyExchange(
        string Method,
        string Path,
        string? RequestRange,
        int StatusCode,
        long? ContentRangeFrom,
        long? ContentRangeTo,
        long? ContentRangeLength,
        long ResponseBytes);
}
