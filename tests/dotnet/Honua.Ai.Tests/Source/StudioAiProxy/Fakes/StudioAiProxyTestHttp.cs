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
