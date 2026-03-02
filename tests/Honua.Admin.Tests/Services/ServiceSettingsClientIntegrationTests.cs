// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Admin.Services;
using Honua.Sdk.Admin;
using Honua.Sdk.Admin.Models;

namespace Honua.Admin.Tests.Services;

public sealed class ServiceSettingsClientIntegrationTests
{
    private static readonly JsonSerializerOptions _camelCaseOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UpdateAccessPolicyAsync_SdkBackedPath_ReturnsWrappedResult()
    {
        HttpMethod? observedMethod = null;
        string? observedPath = null;
        string? observedBody = null;

        var updated = new
        {
            serviceName = "default",
            enabledProtocols = new[] { "FeatureServer" },
            availableProtocols = new[] { "FeatureServer", "MapServer" },
            accessPolicy = new
            {
                allowAnonymous = false,
                allowAnonymousWrite = false,
                allowedRoles = new[] { "reader" },
                allowedWriteRoles = new[] { "writer" }
            },
            mapServer = new
            {
                maxImageWidth = 4096,
                maxImageHeight = 4096,
                defaultImageWidth = 400,
                defaultImageHeight = 300,
                defaultDpi = 96,
                defaultFormat = "png",
                defaultTransparent = true,
                maxFeaturesPerLayer = 10000
            }
        };

        var sdkClient = new HonuaAdminClient(new HttpClient(new MockHttpHandler(async request =>
        {
            observedMethod = request.Method;
            observedPath = request.RequestUri?.AbsolutePath;
            observedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return CreateJsonResponse(updated);
        }))
        {
            BaseAddress = new Uri("http://localhost:5000")
        });

        var client = new ServiceSettingsClient(sdkClient);

        var result = await client.UpdateAccessPolicyAsync("default", new UpdateAccessPolicyRequest
        {
            AllowAnonymous = false,
            AllowAnonymousWrite = false,
            AllowedRoles = ["reader"],
            AllowedWriteRoles = ["writer"]
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Null(result.Message);
        Assert.Equal(HttpMethod.Put, observedMethod);
        Assert.Equal("/api/v1/admin/services/default/access-policy", observedPath);
        Assert.NotNull(observedBody);
        Assert.Contains("\"allowAnonymous\":false", observedBody, StringComparison.Ordinal);
        Assert.Contains("\"allowAnonymousWrite\":false", observedBody, StringComparison.Ordinal);
        Assert.Contains("\"allowedRoles\":[\"reader\"]", observedBody, StringComparison.Ordinal);
        Assert.Contains("\"allowedWriteRoles\":[\"writer\"]", observedBody, StringComparison.Ordinal);

        Assert.NotNull(result.Data!.AccessPolicy);
        Assert.Equal(["reader"], result.Data.AccessPolicy!.AllowedRoles ?? []);
        Assert.Equal(["writer"], result.Data.AccessPolicy.AllowedWriteRoles ?? []);
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T data)
    {
        var envelope = new
        {
            success = true,
            data,
            message = (string?)null,
            timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(envelope, _camelCaseOptions);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
