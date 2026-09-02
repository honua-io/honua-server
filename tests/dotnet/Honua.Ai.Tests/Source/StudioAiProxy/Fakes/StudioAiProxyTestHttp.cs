// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Honua.Server.Tests.Features.StudioAiProxy.Fakes;

/// <summary>
/// A canned <see cref="HttpMessageHandler"/> that always returns the supplied body/status,
/// capturing the outgoing request for assertions. Mirrors the fake used by
/// <c>NlQueryPlanProviderTests</c> for the same style of provider-adapter test.
/// </summary>
internal sealed class StudioAiProxyMockHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    private readonly HttpStatusCode _statusCode;

    public StudioAiProxyMockHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _statusCode = statusCode;
    }

    public string? CapturedRequestBody { get; private set; }

    public HttpRequestHeaders? CapturedHeaders { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        CapturedHeaders = request.Headers;

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "text/event-stream")
        };
    }
}

internal sealed class StudioAiProxyMockHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client;

    public StudioAiProxyMockHttpClientFactory(HttpMessageHandler handler) => _client = new HttpClient(handler);

    public HttpClient CreateClient(string name) => _client;

    public void Dispose() => _client.Dispose();
}

internal sealed class StudioAiProxySequenceHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses;

    public StudioAiProxySequenceHttpMessageHandler(params Func<HttpResponseMessage>[] responses)
        => _responses = new Queue<Func<HttpResponseMessage>>(responses);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_responses.Dequeue()());
}

internal sealed class StudioAiProxyStallingStream : Stream
{
    private readonly byte[] _prefix;
    private int _position;

    public StudioAiProxyStallingStream(string prefix) => _prefix = Encoding.UTF8.GetBytes(prefix);

    public bool WasDisposed { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        WasDisposed = true;
        return base.DisposeAsync();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
