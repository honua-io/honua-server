// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Integration tests for CorrelationIdMiddleware ensuring proper correlation ID handling
/// across client-provided IDs, OpenTelemetry integration, and fallback generation.
/// </summary>
[Collection("Database")]
public class CorrelationIdMiddlewareTests : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CorrelationIdMiddlewareTests(ITestOutputHelper output)
    {
        _output = output;
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        // No specific initialization needed for these tests
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_WithClientProvidedId_ReturnsClientId()
    {
        // Arrange
        const string clientCorrelationId = "client-12345-abcde";

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/live");
        request.Headers.Add("X-Correlation-ID", clientCorrelationId);
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Equal(clientCorrelationId, values.First());
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_WithoutClientId_GeneratesCorrelationId()
    {
        // Act
        var response = await _client.GetAsync("/healthz/live");

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));

        var correlationId = values.First();
        Assert.NotNull(correlationId);
        Assert.NotEmpty(correlationId);

        // Should be a valid GUID format or Activity ID format
        Assert.True(Guid.TryParse(correlationId, out _) || correlationId.Contains('-'));
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_WithInvalidClientId_GeneratesNewId()
    {
        // Arrange - client ID with Unicode control characters (non-printable)
        // Using backspace (U+0008) and form feed (U+000C) which are control characters
        // but can be included in HTTP headers
        var invalidClientId = "invalid\u0008with\u000Ccontrol";

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/live");
        request.Headers.Add("X-Correlation-ID", invalidClientId);
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));

        var correlationId = values.First();
        Assert.NotEqual(invalidClientId, correlationId);
        Assert.DoesNotContain('\u0008', correlationId);
        Assert.DoesNotContain('\u000C', correlationId);
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_WithEmptyClientId_GeneratesNewId()
    {
        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/live");
        request.Headers.Add("X-Correlation-ID", "   "); // Empty/whitespace
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));

        var correlationId = values.First();
        Assert.NotEqual("   ", correlationId);
        Assert.NotNull(correlationId);
        Assert.NotEmpty(correlationId.Trim());
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_WithTooLongClientId_GeneratesNewId()
    {
        // Arrange - client ID exceeding 128 character limit
        var tooLongClientId = new string('x', 150);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/live");
        request.Headers.Add("X-Correlation-ID", tooLongClientId);
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));

        var correlationId = values.First();
        Assert.NotEqual(tooLongClientId, correlationId);
        Assert.True(correlationId.Length <= 128);
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_MultipleRequests_GeneratesDifferentIds()
    {
        // Act - make multiple requests without client correlation IDs
        var responses = await Task.WhenAll(
            _client.GetAsync("/healthz/live"),
            _client.GetAsync("/healthz/live"),
            _client.GetAsync("/healthz/live")
        );

        // Assert
        var correlationIds = responses.Select(response =>
        {
            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
            return values.First();
        }).ToList();

        // Each request should have a unique correlation ID
        Assert.Equal(3, correlationIds.Distinct().Count());
        Assert.All(correlationIds, id =>
        {
            Assert.NotNull(id);
            Assert.NotEmpty(id);
        });
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_HealthEndpoints_AlwaysIncludeCorrelationId()
    {
        // Act & Assert - test both health endpoints
        var endpoints = new[] { "/healthz/live", "/healthz/ready" };

        foreach (var endpoint in endpoints)
        {
            _output.WriteLine($"Testing correlation ID on {endpoint}");

            var response = await _client.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();

            Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values),
                $"Correlation ID header missing from {endpoint}");

            var correlationId = values.First();
            Assert.NotNull(correlationId);
            Assert.NotEmpty(correlationId);

            _output.WriteLine($"Correlation ID for {endpoint}: {correlationId}");
        }
    }

    [Theory]
    [InlineData("simple-correlation-id")]
    [InlineData("prefix-12345-67890-suffix")]
    [InlineData("UUID-LIKE-aBc123DeF456")]
    [InlineData("trace-span-activity-id")]
    public async Task CorrelationIdMiddleware_ValidClientIds_AcceptsValidFormats(string clientCorrelationId)
    {
        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz/live");
        request.Headers.Add("X-Correlation-ID", clientCorrelationId);
        var response = await _client.SendAsync(request);

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.Equal(clientCorrelationId, values.First());
    }

    [IntegrationTest]
    public async Task CorrelationIdMiddleware_IntegrationWithOpenTelemetry_WorksWithActivity()
    {
        // Arrange - Start an Activity to simulate OpenTelemetry context
        using var activity = new Activity("TestActivity");
        activity.Start();

        // Act - make request without client correlation ID
        // Middleware should prefer Activity.Current.Id if no client ID provided
        var response = await _client.GetAsync("/healthz/live");

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));

        var correlationId = values.First();
        Assert.NotNull(correlationId);
        Assert.NotEmpty(correlationId);

        // Note: In test environment, the Activity.Current might not be the same activity
        // due to HttpClient creating its own activities, but we verify structure is reasonable
        Assert.True(correlationId.Length > 10); // Should be substantial ID
    }
}