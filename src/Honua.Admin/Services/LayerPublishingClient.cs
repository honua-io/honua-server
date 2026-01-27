// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

public interface ILayerPublishingClient
{
    Task<ApiResult<TableDiscoveryResponse>> GetTablesAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<ApiResult<IReadOnlyList<PublishedLayerSummary>>> GetPublishedLayersAsync(Guid connectionId, string? serviceName = null, CancellationToken cancellationToken = default);
    Task<ApiResult<PublishedLayerSummary>> PublishLayerAsync(Guid connectionId, PublishLayerRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<PublishedLayerSummary>> SetLayerEnabledAsync(Guid connectionId, int layerId, bool enabled, string? serviceName = null, CancellationToken cancellationToken = default);
    Task<ApiResult<IReadOnlyList<PublishedLayerSummary>>> SetServiceLayersEnabledAsync(Guid connectionId, bool enabled, string? serviceName = null, CancellationToken cancellationToken = default);
}

internal sealed class LayerPublishingClient : ILayerPublishingClient
{
    private readonly HttpClient _httpClient;

    public LayerPublishingClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<TableDiscoveryResponse>> GetTablesAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"connections/{connectionId}/tables", cancellationToken);
        return await ApiResponseReader.ReadUnwrappedAsync<TableDiscoveryResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<IReadOnlyList<PublishedLayerSummary>>> GetPublishedLayersAsync(
        Guid connectionId,
        string? serviceName = null,
        CancellationToken cancellationToken = default)
    {
        var path = $"connections/{connectionId}/layers";
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            path += $"?serviceName={Uri.EscapeDataString(serviceName)}";
        }

        var response = await _httpClient.GetAsync(path, cancellationToken);
        var result = await ApiResponseReader.ReadWrappedAsync<List<PublishedLayerSummary>>(response, cancellationToken);

        return result.Success
            ? ApiResult.Ok<IReadOnlyList<PublishedLayerSummary>>(result.Data ?? new List<PublishedLayerSummary>(), result.Message)
            : ApiResult.Fail<IReadOnlyList<PublishedLayerSummary>>(result.Message);
    }

    public async Task<ApiResult<PublishedLayerSummary>> PublishLayerAsync(
        Guid connectionId,
        PublishLayerRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync($"connections/{connectionId}/layers", request, cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<PublishedLayerSummary>(response, cancellationToken);
    }

    public async Task<ApiResult<PublishedLayerSummary>> SetLayerEnabledAsync(
        Guid connectionId,
        int layerId,
        bool enabled,
        string? serviceName = null,
        CancellationToken cancellationToken = default)
    {
        var path = $"connections/{connectionId}/layers/{layerId}/enabled";
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            path += $"?serviceName={Uri.EscapeDataString(serviceName)}";
        }

        var request = new LayerEnabledRequest { Enabled = enabled };
        var response = await _httpClient.PutAsJsonAsync(path, request, cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<PublishedLayerSummary>(response, cancellationToken);
    }

    public async Task<ApiResult<IReadOnlyList<PublishedLayerSummary>>> SetServiceLayersEnabledAsync(
        Guid connectionId,
        bool enabled,
        string? serviceName = null,
        CancellationToken cancellationToken = default)
    {
        var path = $"connections/{connectionId}/layers/enabled";
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            path += $"?serviceName={Uri.EscapeDataString(serviceName)}";
        }

        var request = new LayerEnabledRequest { Enabled = enabled };
        var response = await _httpClient.PutAsJsonAsync(path, request, cancellationToken);
        var result = await ApiResponseReader.ReadWrappedAsync<List<PublishedLayerSummary>>(response, cancellationToken);

        return result.Success
            ? ApiResult.Ok<IReadOnlyList<PublishedLayerSummary>>(result.Data ?? new List<PublishedLayerSummary>(), result.Message)
            : ApiResult.Fail<IReadOnlyList<PublishedLayerSummary>>(result.Message);
    }

}
