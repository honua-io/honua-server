// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

namespace Honua.Sdk.Admin.Tests.Fixtures;

/// <summary>
/// Shared test helpers for creating mock responses and clients.
/// </summary>
internal static class TestHelpers
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static HonuaAdminClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mockHandler = new MockHttpHandler(handler);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("http://localhost:5000")
        };
        return new HonuaAdminClient(httpClient);
    }

    public static HttpResponseMessage CreateJsonResponse<T>(T data, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var envelope = new
        {
            success = true,
            data,
            message = (string?)null,
            timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(envelope, CamelCaseOptions);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage CreateRawJsonResponse<T>(T data, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(data, CamelCaseOptions);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage CreateErrorResponse(HttpStatusCode statusCode, string message)
    {
        var body = new
        {
            success = false,
            message,
            timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(body, CamelCaseOptions);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage CreateProblemResponse(HttpStatusCode statusCode, string title, string detail)
    {
        var body = new
        {
            title,
            detail,
            status = (int)statusCode
        };

        var json = JsonSerializer.Serialize(body, CamelCaseOptions);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
