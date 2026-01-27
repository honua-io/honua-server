// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
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
        var response = await _httpClient.GetAsync($"metadata/layers/{layerId}/style", cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    public async Task<ApiResult<LayerStyleResponse>> UpdateLayerStyleAsync(
        int layerId,
        LayerStyleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(request, AdminJsonContext.Default.LayerStyleUpdateRequest);
        var response = await _httpClient.PutAsync($"metadata/layers/{layerId}/style", content, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    private static async Task<ApiResult<LayerStyleResponse>> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return ApiResult.Fail<LayerStyleResponse>(error ?? response.ReasonPhrase ?? "Request failed.");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return ApiResult.Fail<LayerStyleResponse>("Empty response from server.");
        }

        ApiResponse<LayerStyleResponse>? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize(payload, AdminJsonContext.Default.ApiResponseLayerStyleResponse);
        }
        catch (JsonException)
        {
            return ApiResult.Fail<LayerStyleResponse>("Unable to parse response from server.");
        }

        if (apiResponse is null)
        {
            return ApiResult.Fail<LayerStyleResponse>("Unable to parse response from server.");
        }

        if (!apiResponse.Success)
        {
            return ApiResult.Fail<LayerStyleResponse>(apiResponse.Message ?? "Request failed.");
        }

        return ApiResult.Ok(apiResponse.Data, apiResponse.Message);
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
