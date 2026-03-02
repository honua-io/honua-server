// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Sdk.Admin.Exceptions;
using Honua.Sdk.Admin.Tests.Fixtures;
using Microsoft.Extensions.Options;

namespace Honua.Sdk.Admin.Tests;

public sealed class HonuaAdminClientTests
{
    [Fact]
    public async Task AuthHandler_AddsApiKeyHeader()
    {
        string? capturedApiKey = null;

        var options = Options.Create(new HonuaAdminClientOptions
        {
            ApiKey = "test-key-123"
        });

        var innerHandler = new MockHttpHandler(req =>
        {
            if (req.Headers.TryGetValues("X-API-Key", out var values))
            {
                capturedApiKey = values.First();
            }

            return Task.FromResult(TestHelpers.CreateJsonResponse(Array.Empty<object>()));
        });

        var authHandler = new HonuaAdminAuthHandler(options)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("http://localhost:5000")
        };

        var client = new HonuaAdminClient(httpClient);
        await client.ListServicesAsync();

        Assert.Equal("test-key-123", capturedApiKey);
    }

    [Fact]
    public async Task AuthHandler_AddsBearerToken()
    {
        string? capturedAuth = null;

        var options = Options.Create(new HonuaAdminClientOptions
        {
            BearerToken = "my-jwt-token"
        });

        var innerHandler = new MockHttpHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(TestHelpers.CreateJsonResponse(Array.Empty<object>()));
        });

        var authHandler = new HonuaAdminAuthHandler(options)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("http://localhost:5000")
        };

        var client = new HonuaAdminClient(httpClient);
        await client.ListServicesAsync();

        Assert.Equal("Bearer my-jwt-token", capturedAuth);
    }

    [Fact]
    public async Task AuthHandler_AddsBothHeaders()
    {
        string? capturedApiKey = null;
        string? capturedAuth = null;

        var options = Options.Create(new HonuaAdminClientOptions
        {
            ApiKey = "admin-key",
            BearerToken = "jwt-token"
        });

        var innerHandler = new MockHttpHandler(req =>
        {
            if (req.Headers.TryGetValues("X-API-Key", out var apiValues))
            {
                capturedApiKey = apiValues.First();
            }

            capturedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(TestHelpers.CreateJsonResponse(Array.Empty<object>()));
        });

        var authHandler = new HonuaAdminAuthHandler(options)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("http://localhost:5000")
        };

        var client = new HonuaAdminClient(httpClient);
        await client.ListServicesAsync();

        Assert.Equal("admin-key", capturedApiKey);
        Assert.Equal("Bearer jwt-token", capturedAuth);
    }

    [Fact]
    public async Task Error400_ThrowsHonuaAdminApiException()
    {
        var client = TestHelpers.CreateClient(_ =>
            Task.FromResult(TestHelpers.CreateErrorResponse(HttpStatusCode.BadRequest, "Invalid request")));

        var ex = await Assert.ThrowsAsync<HonuaAdminApiException>(
            () => client.ListServicesAsync());

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("Invalid request", ex.Message);
        Assert.NotNull(ex.ResponseBody);
    }

    [Fact]
    public async Task Error404_ThrowsHonuaAdminApiException()
    {
        var client = TestHelpers.CreateClient(_ =>
            Task.FromResult(TestHelpers.CreateErrorResponse(HttpStatusCode.NotFound, "Not found")));

        var ex = await Assert.ThrowsAsync<HonuaAdminApiException>(
            () => client.GetServiceSettingsAsync("nonexistent"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("Not found", ex.Message);
    }

    [Fact]
    public async Task Error409_ThrowsHonuaAdminApiException()
    {
        var client = TestHelpers.CreateClient(_ =>
            Task.FromResult(TestHelpers.CreateErrorResponse(HttpStatusCode.Conflict, "Resource conflict")));

        var ex = await Assert.ThrowsAsync<HonuaAdminApiException>(
            () => client.DeleteConnectionAsync("some-id"));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal("Resource conflict", ex.Message);
    }

    [Fact]
    public async Task Error412_ThrowsHonuaAdminApiException()
    {
        var client = TestHelpers.CreateClient(_ =>
            Task.FromResult(TestHelpers.CreateErrorResponse(HttpStatusCode.PreconditionFailed, "ETag precondition failed.")));

        var ex = await Assert.ThrowsAsync<HonuaAdminApiException>(
            () => client.UpdateMetadataResourceAsync(
                "Layer", "default", "test",
                new Models.MetadataResource
                {
                    Spec = JsonDocument.Parse("{}").RootElement
                },
                ifMatch: "\"old-etag\""));

        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);
        Assert.Equal("ETag precondition failed.", ex.Message);
    }

    [Fact]
    public async Task Error428_ThrowsHonuaAdminApiException()
    {
        var client = TestHelpers.CreateClient(_ =>
            Task.FromResult(TestHelpers.CreateErrorResponse(
                (HttpStatusCode)428, "If-Match header is required.")));

        var ex = await Assert.ThrowsAsync<HonuaAdminApiException>(
            () => client.DeleteMetadataResourceAsync("Layer", "default", "test"));

        Assert.Equal((HttpStatusCode)428, ex.StatusCode);
        Assert.Equal("If-Match header is required.", ex.Message);
    }

    [Fact]
    public async Task ProblemDetails_ExtractsDetailMessage()
    {
        var client = TestHelpers.CreateClient(_ =>
            Task.FromResult(TestHelpers.CreateProblemResponse(
                HttpStatusCode.InternalServerError,
                "Internal Server Error",
                "An error occurred while processing the request.")));

        var ex = await Assert.ThrowsAsync<HonuaAdminApiException>(
            () => client.GetConfigAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal("An error occurred while processing the request.", ex.Message);
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsJsonElement()
    {
        var config = new { server = new { port = 5000, env = "development" } };

        var client = TestHelpers.CreateClient(req =>
        {
            Assert.Contains("/admin/config", req.RequestUri!.PathAndQuery);
            return Task.FromResult(TestHelpers.CreateRawJsonResponse(config));
        });

        var result = await client.GetConfigAsync();

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }
}
