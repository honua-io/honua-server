// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
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
    private readonly HttpClient _httpClient;

    public EsriImportClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<EsriDiscoverResponse>> DiscoverAsync(EsriDiscoverRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("import/esri/discover", request, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<EsriDiscoverResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportJobResponse>> StartImportAsync(EsriStartImportRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("import/esri/start", request, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<EsriImportJobResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportProgress>> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"import/esri/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<EsriImportProgress>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportJobsResponse>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("import/esri/jobs", cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<EsriImportJobsResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<EsriImportCancelResponse>> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"import/esri/jobs/{Uri.EscapeDataString(jobId)}/cancel", null, cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<EsriImportCancelResponse>(response, cancellationToken);
    }
}
