// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Sockets;
using System.Net;
using System.Text;

namespace Honua.TestKit.Security;

/// <summary>Real local HTTP endpoint for retry and streaming-lifetime tests; never contacts a model service.</summary>
public sealed class LoopbackModelEndpoint : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serve;
    private readonly bool _holdBody;
    private int _requests;
    public LoopbackModelEndpoint(bool holdBody = false)
    {
        _holdBody = holdBody;
        _listener.Start();
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1");
        _serve = ServeAsync();
    }
    public Uri Address { get; }
    public int Requests => Volatile.Read(ref _requests);
    public TaskCompletionSource BodyOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                if (await reader.ReadLineAsync(_stop.Token) is null) continue;
                var length = 0;
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
                if (length > 0)
                {
                    var body = new char[length];
                    var consumed = 0;
                    while (consumed < length)
                    {
                        var read = await reader.ReadAsync(body.AsMemory(consumed), _stop.Token);
                        if (read == 0) throw new EndOfStreamException();
                        consumed += read;
                    }
                }
                Interlocked.Increment(ref _requests);
                var response = _holdBody
                    ? "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: 9999\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                BodyOpened.TrySetResult();
                if (_holdBody)
                    await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _serve.WaitAsync(TimeSpan.FromSeconds(3));
        _listener.Stop();
        _stop.Dispose();
    }
}
