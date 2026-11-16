// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;

namespace Honua.Geoprocessing.Cli.Tests;

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(string Suffix, HttpStatusCode Status, string Body)> _responses = [];

    public List<RecordedRequest> Requests { get; } = [];

    public RecordingHttpMessageHandler RespondOk(string suffix, string dataJson)
    {
        _responses.Add((suffix, HttpStatusCode.OK, $"{{\"success\":true,\"data\":{dataJson}}}"));
        return this;
    }

    public RecordingHttpMessageHandler RespondCreated(string suffix, string dataJson)
    {
        _responses.Add((suffix, HttpStatusCode.Created, $"{{\"success\":true,\"data\":{dataJson}}}"));
        return this;
    }

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

internal sealed record RecordedRequest(
    string Method,
    string Path,
    string? ApiKeyHeader,
    string? AuthorizationHeader,
    string? Body);
