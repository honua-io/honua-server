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
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public LayerStyleClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<LayerStyleResponse>> GetLayerStyleAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"metadata/layers/{layerId}/style", cancellationToken);
        return await ReadResponseAsync<LayerStyleResponse>(response, cancellationToken);
    }

    public async Task<ApiResult<LayerStyleResponse>> UpdateLayerStyleAsync(
        int layerId,
        LayerStyleUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"metadata/layers/{layerId}/style", request, cancellationToken);
        return await ReadResponseAsync<LayerStyleResponse>(response, cancellationToken);
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

        ApiResponse<T>? apiResponse;
        try
        {
            apiResponse = JsonSerializer.Deserialize<ApiResponse<T>>(payload, _jsonOptions);
        }
        catch (JsonException)
        {
            return ApiResult.Fail<T>("Unable to parse response from server.");
        }

        if (apiResponse is null)
        {
            return ApiResult.Fail<T>("Unable to parse response from server.");
        }

        if (!apiResponse.Success)
        {
            return ApiResult.Fail<T>(apiResponse.Message ?? "Request failed.");
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
