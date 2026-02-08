// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

/// <summary>
/// Client for the service settings admin API.
/// </summary>
public interface IServiceSettingsClient
{
    /// <summary>Lists all services.</summary>
    Task<ApiResult<ServiceSummary[]>> ListServicesAsync(CancellationToken ct = default);

    /// <summary>Gets settings for a service.</summary>
    Task<ApiResult<ServiceSettingsResponse>> GetSettingsAsync(string serviceName, CancellationToken ct = default);

    /// <summary>Updates enabled protocols for a service.</summary>
    Task<ApiResult<ServiceSettingsResponse>> UpdateProtocolsAsync(string serviceName, string[] protocols, CancellationToken ct = default);

    /// <summary>Updates MapServer settings for a service.</summary>
    Task<ApiResult<ServiceSettingsResponse>> UpdateMapServerSettingsAsync(string serviceName, UpdateMapServerSettingsRequest request, CancellationToken ct = default);
}

/// <summary>
/// HTTP implementation of <see cref="IServiceSettingsClient"/>.
/// </summary>
internal sealed class ServiceSettingsClient : IServiceSettingsClient
{
    private readonly HttpClient _httpClient;

    public ServiceSettingsClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<ServiceSummary[]>> ListServicesAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync("api/v1/admin/services", ct);
        return await ApiResponseReader.ReadWrappedAsync<ServiceSummary[]>(
            response, ct, AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<ServiceSettingsResponse>> GetSettingsAsync(string serviceName, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync($"api/v1/admin/services/{Uri.EscapeDataString(serviceName)}/settings", ct);
        return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(
            response, ct, AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<ServiceSettingsResponse>> UpdateProtocolsAsync(string serviceName, string[] protocols, CancellationToken ct = default)
    {
        var request = new UpdateProtocolsRequest { EnabledProtocols = protocols };
        using var content = JsonContent.Create(request, AdminJsonContext.Default.UpdateProtocolsRequest);
        using var response = await _httpClient.PutAsync(
            $"api/v1/admin/services/{Uri.EscapeDataString(serviceName)}/protocols", content, ct);
        return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(
            response, ct, AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<ServiceSettingsResponse>> UpdateMapServerSettingsAsync(
        string serviceName, UpdateMapServerSettingsRequest request, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(request, AdminJsonContext.Default.UpdateMapServerSettingsRequest);
        using var response = await _httpClient.PutAsync(
            $"api/v1/admin/services/{Uri.EscapeDataString(serviceName)}/mapserver", content, ct);
        return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(
            response, ct, AdminJsonContext.Default.Options);
    }
}
