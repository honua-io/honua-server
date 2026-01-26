// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

public interface ISecureConnectionsClient
{
    Task<ApiResult<IReadOnlyList<SecureConnectionSummary>>> GetConnectionsAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<SecureConnectionDetail>> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<ApiResult<SecureConnectionSummary>> CreateConnectionAsync(CreateSecureConnectionRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<ConnectionTestResult>> TestDraftConnectionAsync(CreateSecureConnectionRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<SecureConnectionSummary>> UpdateConnectionAsync(Guid connectionId, UpdateSecureConnectionRequest request, CancellationToken cancellationToken = default);
    Task<ApiResult<bool>> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<ApiResult<ConnectionTestResult>> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);
}

public sealed record ApiResult<T>(bool Success, T? Data, string? Message);

public static class ApiResult
{
    public static ApiResult<T> Ok<T>(T? data, string? message = null) => new(true, data, message);

    public static ApiResult<T> Fail<T>(string? message) => new(false, default, message);
}

internal sealed class SecureConnectionsClient : ISecureConnectionsClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public SecureConnectionsClient(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClient = httpClientFactory.CreateClient("AdminApi");
    }

    public async Task<ApiResult<IReadOnlyList<SecureConnectionSummary>>> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("connections", cancellationToken);
        var result = await ReadResponseAsync<List<SecureConnectionSummary>>(response, cancellationToken);

        return result.Success
            ? ApiResult.Ok<IReadOnlyList<SecureConnectionSummary>>(result.Data ?? new List<SecureConnectionSummary>(), result.Message)
            : ApiResult.Fail<IReadOnlyList<SecureConnectionSummary>>(result.Message);
    }

    public async Task<ApiResult<SecureConnectionDetail>> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"connections/{connectionId}", cancellationToken);
        return await ReadResponseAsync<SecureConnectionDetail>(response, cancellationToken);
    }

    public async Task<ApiResult<SecureConnectionSummary>> CreateConnectionAsync(CreateSecureConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("connections", request, cancellationToken);
        return await ReadResponseAsync<SecureConnectionSummary>(response, cancellationToken);
    }

    public async Task<ApiResult<ConnectionTestResult>> TestDraftConnectionAsync(CreateSecureConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("connections/test", request, cancellationToken);
        return await ReadResponseAsync<ConnectionTestResult>(response, cancellationToken);
    }

    public async Task<ApiResult<SecureConnectionSummary>> UpdateConnectionAsync(Guid connectionId, UpdateSecureConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"connections/{connectionId}", request, cancellationToken);
        return await ReadResponseAsync<SecureConnectionSummary>(response, cancellationToken);
    }

    public async Task<ApiResult<bool>> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.DeleteAsync($"connections/{connectionId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            return ApiResult.Fail<bool>(error ?? "Failed to delete connection.");
        }

        return ApiResult.Ok(true, "Connection deleted.");
    }

    public async Task<ApiResult<ConnectionTestResult>> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"connections/{connectionId}/test");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadResponseAsync<ConnectionTestResult>(response, cancellationToken);
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
