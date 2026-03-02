// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Sdk.Admin;
using Honua.Sdk.Admin.Exceptions;
using Honua.Sdk.Admin.Models;

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
    private readonly IHonuaAdminClient _adminClient;

    public ServiceSettingsClient(IHonuaAdminClient adminClient)
    {
        _adminClient = adminClient ?? throw new ArgumentNullException(nameof(adminClient));
    }

    public async Task<ApiResult<ServiceSummary[]>> ListServicesAsync(CancellationToken ct = default)
    {
        try
        {
            var services = await _adminClient.ListServicesAsync(ct);
            return ApiResult.Ok(services.ToArray());
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
            var settings = await _adminClient.GetServiceSettingsAsync(serviceName, ct);
            return ApiResult.Ok(settings);
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
            var updated = await _adminClient.UpdateProtocolsAsync(serviceName, protocols, ct);
            return ApiResult.Ok(updated);
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
            var updated = await _adminClient.UpdateMapServerSettingsAsync(serviceName, request, ct);
            return ApiResult.Ok(updated);
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
            var updated = await _adminClient.UpdateAccessPolicyAsync(serviceName, request, ct);
            return ApiResult.Ok(updated);
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
            var updated = await _adminClient.UpdateTimeInfoAsync(serviceName, request, ct);
            return ApiResult.Ok(updated);
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
            var updated = await _adminClient.UpdateLayerMetadataAsync(serviceName, layerId, request, ct);
            return ApiResult.Ok(updated);
        }
        catch (Exception ex)
        {
            return ApiResult.Fail<LayerMetadataResponse>(GetFailureMessage(ex, "Failed to update layer metadata."));
        }
    }

    private static string GetFailureMessage(Exception ex, string fallbackMessage)
    {
        return ex switch
        {
            HonuaAdminApiException apiException => apiException.Message,
            HonuaAdminOperationException operationException => operationException.Message,
            _ => fallbackMessage
        };
    }
}
