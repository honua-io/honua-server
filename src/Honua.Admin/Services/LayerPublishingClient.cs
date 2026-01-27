// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public LayerPublishingClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<TableDiscoveryResponse>> GetTablesAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"connections/{connectionId}/tables", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return ApiResult.Fail<TableDiscoveryResponse>(error ?? "Failed to load tables.");
        }

        var payload = await response.Content.ReadFromJsonAsync<TableDiscoveryResponse>(cancellationToken: cancellationToken);
        if (payload == null)
        {
            return ApiResult.Fail<TableDiscoveryResponse>("Unable to parse table discovery response.");
        }

        return ApiResult.Ok(payload);
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
        var result = await ReadResponseAsync<List<PublishedLayerSummary>>(response, cancellationToken);

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
        return await ReadResponseAsync<PublishedLayerSummary>(response, cancellationToken);
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
        return await ReadResponseAsync<PublishedLayerSummary>(response, cancellationToken);
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
        var result = await ReadResponseAsync<List<PublishedLayerSummary>>(response, cancellationToken);

        return result.Success
            ? ApiResult.Ok<IReadOnlyList<PublishedLayerSummary>>(result.Data ?? new List<PublishedLayerSummary>(), result.Message)
            : ApiResult.Fail<IReadOnlyList<PublishedLayerSummary>>(result.Message);
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
