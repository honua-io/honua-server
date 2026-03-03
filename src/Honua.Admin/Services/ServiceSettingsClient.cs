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

    /// <summary>Updates the access policy for a service.</summary>
    Task<ApiResult<ServiceSettingsResponse>> UpdateAccessPolicyAsync(string serviceName, UpdateAccessPolicyRequest request, CancellationToken ct = default);

    /// <summary>Updates the time info for a service.</summary>
    Task<ApiResult<ServiceSettingsResponse>> UpdateTimeInfoAsync(string serviceName, UpdateTimeInfoRequest request, CancellationToken ct = default);

    /// <summary>Updates metadata for a specific layer.</summary>
    Task<ApiResult<LayerMetadataResponse>> UpdateLayerMetadataAsync(string serviceName, int layerId, UpdateLayerMetadataRequest request, CancellationToken ct = default);
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
        try
        {
            using var response = await _httpClient.GetAsync("services/", ct);
            var result = await ApiResponseReader.ReadWrappedAsync<ServiceSummary[]>(response, ct);
            return result.Success
                ? ApiResult.Ok(result.Data ?? [])
                : ApiResult.Fail<ServiceSummary[]>(result.Message ?? "Failed to load services.");
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<ServiceSummary[]>(GetFailureMessage(ex, "Failed to load services."));
        }
    }

    public async Task<ApiResult<ServiceSettingsResponse>> GetSettingsAsync(string serviceName, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"services/{Uri.EscapeDataString(serviceName)}/settings",
                ct);
            return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(response, ct);
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<ServiceSettingsResponse>(GetFailureMessage(ex, "Failed to load service settings."));
        }
    }

    public async Task<ApiResult<ServiceSettingsResponse>> UpdateProtocolsAsync(string serviceName, string[] protocols, CancellationToken ct = default)
    {
        try
        {
            var request = new UpdateProtocolsRequest { EnabledProtocols = protocols ?? [] };
            using var response = await _httpClient.PutAsJsonAsync(
                $"services/{Uri.EscapeDataString(serviceName)}/protocols",
                request,
                ct);
            return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(response, ct);
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<ServiceSettingsResponse>(GetFailureMessage(ex, "Failed to update protocols."));
        }
    }

    public async Task<ApiResult<ServiceSettingsResponse>> UpdateMapServerSettingsAsync(
        string serviceName, UpdateMapServerSettingsRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"services/{Uri.EscapeDataString(serviceName)}/mapserver",
                request,
                ct);
            return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(response, ct);
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<ServiceSettingsResponse>(GetFailureMessage(ex, "Failed to update MapServer settings."));
        }
    }

    public async Task<ApiResult<ServiceSettingsResponse>> UpdateAccessPolicyAsync(
        string serviceName, UpdateAccessPolicyRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"services/{Uri.EscapeDataString(serviceName)}/access-policy",
                request,
                ct);
            return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(response, ct);
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<ServiceSettingsResponse>(GetFailureMessage(ex, "Failed to update access policy."));
        }
    }

    public async Task<ApiResult<ServiceSettingsResponse>> UpdateTimeInfoAsync(
        string serviceName, UpdateTimeInfoRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"services/{Uri.EscapeDataString(serviceName)}/timeinfo",
                request,
                ct);
            return await ApiResponseReader.ReadWrappedAsync<ServiceSettingsResponse>(response, ct);
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<ServiceSettingsResponse>(GetFailureMessage(ex, "Failed to update time info."));
        }
    }

    public async Task<ApiResult<LayerMetadataResponse>> UpdateLayerMetadataAsync(
        string serviceName, int layerId, UpdateLayerMetadataRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"services/{Uri.EscapeDataString(serviceName)}/layers/{layerId}/metadata",
                request,
                ct);
            return await ApiResponseReader.ReadWrappedAsync<LayerMetadataResponse>(response, ct);
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<LayerMetadataResponse>(GetFailureMessage(ex, "Failed to update layer metadata."));
        }
    }

    private static string GetFailureMessage(Exception ex, string fallbackMessage)
    {
        return string.IsNullOrWhiteSpace(ex.Message) ? fallbackMessage : ex.Message;
    }
}
