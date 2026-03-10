// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Postgres.Features.Geocoding;
using Honua.Postgres.Features.Geocoding.Providers;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Honua.Postgres.Tests.Features.Geocoding.Providers;

/// <summary>
/// Unit tests for EsriTokenManager
/// </summary>
public sealed class EsriTokenManagerTests : IAsyncDisposable
{
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger> _loggerMock;
    private readonly EsriGeocodingOptions _options;
    private readonly EsriTokenManager _tokenManager;

    public EsriTokenManagerTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _loggerMock = new Mock<ILogger>();

        _options = new EsriGeocodingOptions
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            TokenEndpoint = "https://www.arcgis.com/sharing/rest/oauth2/token",
            TokenCacheDurationMinutes = 60
        };

        _tokenManager = new EsriTokenManager(_httpClient, _options, _loggerMock.Object);
    }

    [Fact]
    public async Task GetTokenAsync_WithValidCredentials_ShouldReturnToken()
    {
        // Arrange
        var tokenResponse = new EsriTokenResponse
        {
            AccessToken = "test-access-token-12345",
            ExpiresIn = 3600
        };

        SetupHttpResponse(tokenResponse);

        // Act
        var token = await _tokenManager.GetTokenAsync();

        // Assert
        Assert.Equal("test-access-token-12345", token);

        // Verify the request was made with correct parameters
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri!.ToString() == _options.TokenEndpoint),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetTokenAsync_WithCachedToken_ShouldReturnCachedToken()
    {
        // Arrange
        var tokenResponse = new EsriTokenResponse
        {
            AccessToken = "test-access-token-12345",
            ExpiresIn = 3600
        };

        SetupHttpResponse(tokenResponse);

        // Act - First call
        var token1 = await _tokenManager.GetTokenAsync();

        // Act - Second call (should use cached token)
        var token2 = await _tokenManager.GetTokenAsync();

        // Assert
        Assert.Equal(token1, token2);

        // Verify only one HTTP request was made
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetTokenAsync_WithExpiredToken_ShouldRefreshToken()
    {
        // Arrange
        var firstTokenResponse = new EsriTokenResponse
        {
            AccessToken = "first-token",
            ExpiresIn = 1 // 1 second expiry
        };

        var secondTokenResponse = new EsriTokenResponse
        {
            AccessToken = "second-token",
            ExpiresIn = 3600
        };

        var callCount = 0;
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                var response = callCount == 1 ? firstTokenResponse : secondTokenResponse;
                var json = JsonSerializer.Serialize(response);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

        // Act
        var token1 = await _tokenManager.GetTokenAsync();

        // Wait for token to expire
        await Task.Delay(1100);

        var token2 = await _tokenManager.GetTokenAsync();

        // Assert
        Assert.Equal("first-token", token1);
        Assert.Equal("second-token", token2);

        // Verify two HTTP requests were made
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetTokenAsync_WithHttpError_ShouldThrowException()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Invalid credentials")
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _tokenManager.GetTokenAsync());

        Assert.Contains("Failed to obtain OAuth token", exception.Message);
        Assert.Contains("Unauthorized", exception.Message);
    }

    [Fact]
    public async Task GetTokenAsync_WithEsriError_ShouldThrowException()
    {
        // Arrange
        var errorResponse = new EsriTokenResponse
        {
            Error = new EsriTokenError
            {
                Code = 400,
                Message = "Invalid client credentials"
            }
        };

        SetupHttpResponse(errorResponse);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _tokenManager.GetTokenAsync());

        Assert.Contains("OAuth error", exception.Message);
        Assert.Contains("Invalid client credentials", exception.Message);
    }

    [Fact]
    public async Task GetTokenAsync_WithMissingCredentials_ShouldThrowException()
    {
        // Arrange
        var optionsWithoutCredentials = new EsriGeocodingOptions
        {
            ClientId = string.Empty,
            ClientSecret = string.Empty
        };

        var tokenManagerWithoutCredentials = new EsriTokenManager(_httpClient, optionsWithoutCredentials, _loggerMock.Object);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tokenManagerWithoutCredentials.GetTokenAsync());

        Assert.Contains("OAuth ClientId and ClientSecret are required", exception.Message);

        await tokenManagerWithoutCredentials.DisposeAsync();
    }

    [Fact]
    public async Task GetTokenAsync_WithEmptyTokenResponse_ShouldThrowException()
    {
        // Arrange
        var emptyResponse = new EsriTokenResponse
        {
            AccessToken = string.Empty,
            ExpiresIn = 3600
        };

        SetupHttpResponse(emptyResponse);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _tokenManager.GetTokenAsync());

        Assert.Contains("Invalid token response", exception.Message);
    }

    [Fact]
    public async Task GetTokenAsync_WithConcurrentRequests_ShouldOnlyMakeOneRequest()
    {
        // Arrange
        var tokenResponse = new EsriTokenResponse
        {
            AccessToken = "test-access-token",
            ExpiresIn = 3600
        };

        var tcs = new TaskCompletionSource<HttpResponseMessage>();
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(tcs.Task);

        // Act - Start multiple concurrent requests
        var task1 = _tokenManager.GetTokenAsync();
        var task2 = _tokenManager.GetTokenAsync();
        var task3 = _tokenManager.GetTokenAsync();

        // Complete the HTTP request
        var json = JsonSerializer.Serialize(tokenResponse);
        tcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        var results = await Task.WhenAll(task1, task2, task3);

        // Assert
        Assert.All(results, token => Assert.Equal("test-access-token", token));

        // Verify only one HTTP request was made despite concurrent calls
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    private void SetupHttpResponse<T>(T responseObject)
    {
        var json = JsonSerializer.Serialize(responseObject);
        var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);
    }

    public async ValueTask DisposeAsync()
    {
        await _tokenManager.DisposeAsync();
        _httpClient.Dispose();
    }
}