// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

public interface IEsriImportClient
{
    Task<ApiResult<EsriDiscoverResponse>> DiscoverAsync(EsriDiscoverRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<EsriImportJobResponse>> StartImportAsync(EsriStartImportRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<EsriImportProgress>> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);
    Task<ApiResult<EsriImportJobsResponse>> ListJobsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<EsriImportCancelResponse>> CancelJobAsync(string jobId, CancellationToken cancellationToken = default);
}

internal sealed class EsriImportClient : IEsriImportClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public EsriImportClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<EsriDiscoverResponse>> DiscoverAsync(EsriDiscoverRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("import/esri/discover", request, cancellationToken);
        return await ReadResponseAsync<EsriDiscoverResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportJobResponse>> StartImportAsync(EsriStartImportRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("import/esri/start", request, cancellationToken);
        return await ReadResponseAsync<EsriImportJobResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportProgress>> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"import/esri/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
        return await ReadResponseAsync<EsriImportProgress>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportJobsResponse>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("import/esri/jobs", cancellationToken);
        return await ReadResponseAsync<EsriImportJobsResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportCancelResponse>> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"import/esri/jobs/{Uri.EscapeDataString(jobId)}/cancel", null, cancellationToken);
        return await ReadResponseAsync<EsriImportCancelResponse>(response, cancellationToken);
    }

    private static async Task<ApiResult<T>> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return ApiResult.Fail<T>(error ?? response.ReasonPhrase ?? "Request failed.");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return ApiResult.Fail<T>("Empty response from server.");
        }

        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(payload, _jsonOptions);
        }
        catch (JsonException)
        {
            return ApiResult.Fail<T>("Unable to parse response from server.");
        }

        if (result is null)
        {
            return ApiResult.Fail<T>("Unable to parse response from server.");
        }

        return ApiResult.Ok(result);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }

            if (root.TryGetProperty("title", out var title))
            {
                return title.GetString();
            }

            if (root.TryGetProperty("detail", out var detail))
            {
                return detail.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore parsing errors and fall back to default message.
        }

        return null;
    }
}
