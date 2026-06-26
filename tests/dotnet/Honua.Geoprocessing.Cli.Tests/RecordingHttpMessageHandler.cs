// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// A faked <see cref="HttpMessageHandler"/> that records every request (method, path, captured
/// auth headers, and body) and replies with queued canned responses keyed by path suffix. Lets the
/// publish-flow tests assert the call sequence, payload mapping, and auth header fully offline.
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(string Suffix, HttpStatusCode Status, string Body)> _responses = [];

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Queues a 200 response with an <c>ApiResponse</c>-shaped JSON body for the path suffix.</summary>
    public RecordingHttpMessageHandler RespondOk(string suffix, string dataJson)
    {
        _responses.Add((suffix, HttpStatusCode.OK, $"{{\"success\":true,\"data\":{dataJson}}}"));
        return this;
    }

    /// <summary>Queues a 201 response with an <c>ApiResponse</c>-shaped JSON body for the path suffix.</summary>
    public RecordingHttpMessageHandler RespondCreated(string suffix, string dataJson)
    {
        _responses.Add((suffix, HttpStatusCode.Created, $"{{\"success\":true,\"data\":{dataJson}}}"));
        return this;
    }

    /// <summary>Queues a raw status + body response for the path suffix (e.g. an auth failure).</summary>
    public RecordingHttpMessageHandler Respond(string suffix, HttpStatusCode status, string body)
    {
        _responses.Add((suffix, status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        request.Headers.TryGetValues("X-API-Key", out var apiKeyValues);

        Requests.Add(new RecordedRequest(
            request.Method.Method,
            path,
            apiKeyValues is null ? null : string.Join(",", apiKeyValues),
            request.Headers.Authorization?.ToString(),
            body));

        var match = _responses.FirstOrDefault(r => path.EndsWith(r.Suffix, StringComparison.Ordinal));
        if (match.Suffix is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"{{\"success\":false,\"message\":\"no canned response for {path}\"}}")
            };
        }

        return new HttpResponseMessage(match.Status)
        {
            Content = new StringContent(match.Body)
        };
    }
}

/// <summary>A single recorded outbound request.</summary>
internal sealed record RecordedRequest(
    string Method,
    string Path,
    string? ApiKeyHeader,
    string? AuthorizationHeader,
    string? Body);
