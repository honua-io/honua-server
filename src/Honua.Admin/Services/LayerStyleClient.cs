// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

public interface ILayerStyleClient
{
    Task<ApiResult<LayerStyleResponse>> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default);
    Task<ApiResult<LayerStyleResponse>> UpdateLayerStyleAsync(int layerId, LayerStyleUpdateRequest request, CancellationToken cancellationToken = default);
}

internal sealed class LayerStyleClient : ILayerStyleClient
{
    private readonly HttpClient _httpClient;

    public LayerStyleClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<LayerStyleResponse>> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"metadata/layers/{layerId}/style", cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<LayerStyleResponse>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }

    public async Task<ApiResult<LayerStyleResponse>> UpdateLayerStyleAsync(
        int layerId,
        LayerStyleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(request, AdminJsonContext.Default.LayerStyleUpdateRequest);
        using var response = await _httpClient.PutAsync($"metadata/layers/{layerId}/style", content, cancellationToken);
        return await ApiResponseReader.ReadWrappedAsync<LayerStyleResponse>(
            response,
            cancellationToken,
            AdminJsonContext.Default.Options);
    }
}
