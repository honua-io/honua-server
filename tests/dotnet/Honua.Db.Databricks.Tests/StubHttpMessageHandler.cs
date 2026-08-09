// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;

namespace Honua.Databricks.Tests;

/// <summary>
/// Test double that returns queued canned responses in order, recording each request
/// for assertions. Used to drive the Statement Execution submit → poll → chunk flow
/// without a live Databricks workspace.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public StubHttpMessageHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responders.Enqueue(_ =>
        {
            var content = new StringContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(status) { Content = content };
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responders.Count == 0)
        {
            throw new InvalidOperationException("StubHttpMessageHandler received an unexpected request with no queued response.");
        }

        var responder = _responders.Dequeue();
        return Task.FromResult(responder(request));
    }
}
