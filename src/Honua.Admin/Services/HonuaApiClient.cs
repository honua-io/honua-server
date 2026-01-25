// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;

namespace Honua.Admin.Services;

internal sealed class HonuaApiClient
{
    private readonly HttpClient _httpClient;

    public HonuaApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Task<T?> GetAsync<T>(string requestUri, CancellationToken cancellationToken = default)
        => _httpClient.GetFromJsonAsync<T>(requestUri, cancellationToken);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        => _httpClient.SendAsync(request, cancellationToken);
}
