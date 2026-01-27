// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Admin.Models;

namespace Honua.Admin.Services;

internal static class ApiResponseReader
{
    private static readonly JsonSerializerOptions _defaultOptions = new(JsonSerializerDefaults.Web);

    public static async Task<ApiResult<T>> ReadWrappedAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        JsonSerializerOptions? options = null)
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
            apiResponse = JsonSerializer.Deserialize<ApiResponse<T>>(payload, options ?? _defaultOptions);
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

    public static async Task<ApiResult<T>> ReadUnwrappedAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        JsonSerializerOptions? options = null)
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

        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(payload, options ?? _defaultOptions);
        }
        catch (JsonException)
        {
            return ApiResult.Fail<T>("Unable to parse response from server.");
        }

        if (result is null)
        {
            return ApiResult.Fail<T>("Unable to parse response from server.");
        }

        return ApiResult.Ok(result);
    }

    public static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
