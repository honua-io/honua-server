// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

/// <summary>
/// Client for triggering and monitoring Geoservices feature service imports.
/// </summary>
public interface IGeoservicesImportClient
{
    Task<ApiResult<GeoservicesDiscoverResponse>> DiscoverAsync(GeoservicesDiscoverRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<GeoservicesImportJobResponse>> StartImportAsync(GeoservicesStartImportRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<GeoservicesImportProgress>> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);
    Task<ApiResult<GeoservicesImportJobsResponse>> ListJobsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<GeoservicesImportCancelResponse>> CancelJobAsync(string jobId, CancellationToken cancellationToken = default);
}

internal sealed class GeoservicesImportClient : IGeoservicesImportClient
{
    private readonly HttpClient _httpClient;

    public GeoservicesImportClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<GeoservicesDiscoverResponse>> DiscoverAsync(GeoservicesDiscoverRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("import/geoservices/discover", request, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<GeoservicesDiscoverResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<GeoservicesImportJobResponse>> StartImportAsync(GeoservicesStartImportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("import/geoservices/start", request, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<GeoservicesImportJobResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<GeoservicesImportProgress>> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"import/geoservices/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<GeoservicesImportProgress>(response, cancellationToken);
    }

    public async Task<ApiResult<GeoservicesImportJobsResponse>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("import/geoservices/jobs", cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<GeoservicesImportJobsResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<GeoservicesImportCancelResponse>> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync($"import/geoservices/jobs/{Uri.EscapeDataString(jobId)}/cancel", null, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<GeoservicesImportCancelResponse>(response, cancellationToken);
    }
}
