// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Postgres.Features.Geocoding;
using Honua.Postgres.Features.Geocoding.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Xunit;

namespace Honua.Postgres.Tests.Features.Geocoding.Providers;

/// <summary>
/// Unit tests for EsriGeocodeProvider
/// </summary>
public sealed class EsriGeocodeProviderTests : IAsyncDisposable
{
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<EsriGeocodeProvider>> _loggerMock;
    private readonly EsriGeocodingOptions _options;
    private readonly EsriGeocodeProvider _provider;

    public EsriGeocodeProviderTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object)
        {
            BaseAddress = new Uri("https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer/")
        };

        _loggerMock = new Mock<ILogger<EsriGeocodeProvider>>();

        _options = new EsriGeocodingOptions
        {
            BaseUrl = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer",
            ApiKey = "test-api-key",
            TimeoutSeconds = 30,
            MaxResults = 10,
            DefaultSpatialReference = 4326,
            DefaultOutFields = ["Addr_type", "Country", "PlaceName"],
            EnableSuggestions = true,
            EnableBatchGeocoding = true,
            UserAgent = "Honua-Test/1.0"
        };

        var optionsMock = new Mock<IOptions<EsriGeocodingOptions>>();
        optionsMock.Setup(x => x.Value).Returns(_options);

        _provider = new EsriGeocodeProvider(_httpClient, optionsMock.Object, _loggerMock.Object);
    }

    [Fact]
    public void Name_ShouldReturnEsri()
    {
        // Act & Assert
        Assert.Equal("esri", _provider.Name);
    }

    [Fact]
    public void Capabilities_ShouldReflectConfiguration()
    {
        // Act
        var capabilities = _provider.Capabilities;

        // Assert
        Assert.True(capabilities.SupportsForwardGeocode);
        Assert.True(capabilities.SupportsReverseGeocode);
        Assert.True(capabilities.SupportsSuggest);
        Assert.True(capabilities.SupportsBatch);
        Assert.True(capabilities.SupportsStructuredInput);
        Assert.True(capabilities.SupportsBiasing);
        Assert.Equal(10, capabilities.MaxResultsPerRequest);
        Assert.Contains(4326, capabilities.SupportedSpatialReferences);
        Assert.Contains(3857, capabilities.SupportedSpatialReferences);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithValidResponse_ShouldReturnCandidates()
    {
        // Arrange
        var request = new ForwardGeocodeRequest("123 Main St, Seattle, WA", MaxResults: 5);
        var esriResponse = new EsriFindCandidatesResponse
        {
            Candidates = new[]
            {
                new EsriCandidate
                {
                    Address = "123 Main St, Seattle, WA, 98101",
                    Location = new EsriLocation { X = -122.335167, Y = 47.608013 },
                    Score = 95.5,
                    Attributes = new Dictionary<string, object?>
                    {
                        ["Addr_type"] = "PointAddress",
                        ["City"] = "Seattle",
                        ["Region"] = "Washington"
                    }
                }
            }
        };

        SetupHttpResponse(esriResponse);

        // Act
        var result = await _provider.ForwardGeocodeAsync(request);

        // Assert
        Assert.Single(result);
        var candidate = result[0];
        Assert.Equal("123 Main St, Seattle, WA, 98101", candidate.Address);
        Assert.Equal(-122.335167, candidate.X, precision: 6);
        Assert.Equal(47.608013, candidate.Y, precision: 6);
        Assert.Equal(95.5, candidate.Score);
        Assert.Equal(4326, candidate.SpatialReferenceWkid);
        Assert.Equal("PointAddress", candidate.AddressType);
        Assert.Equal("exact", candidate.MatchLevel);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithStructuredAddress_ShouldUseCorrectParameters()
    {
        // Arrange
        var structuredAddress = new StructuredAddress
        {
            AddressNumber = "123",
            StreetName = "Main St",
            City = "Seattle",
            Region = "WA",
            PostalCode = "98101"
        };

        var request = new ForwardGeocodeRequest("", InputType: GeocodeInputType.Structured)
        {
            StructuredAddress = structuredAddress
        };

        var esriResponse = new EsriFindCandidatesResponse { Candidates = [] };
        SetupHttpResponse(esriResponse);

        // Act
        await _provider.ForwardGeocodeAsync(request);

        // Assert
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.Query.Contains("address=123") &&
                req.RequestUri.Query.Contains("address2=Main%20St") &&
                req.RequestUri.Query.Contains("city=Seattle") &&
                req.RequestUri.Query.Contains("region=WA") &&
                req.RequestUri.Query.Contains("postal=98101")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ReverseGeocodeAsync_WithValidResponse_ShouldReturnMatch()
    {
        // Arrange
        var request = new ReverseGeocodeRequest(-122.335167, 47.608013);
        var esriResponse = new EsriReverseGeocodeResponse
        {
            Address = new EsriAddress
            {
                LongLabel = "123 Main St, Seattle, WA, 98101, USA",
                MatchAddress = "123 Main St",
                City = "Seattle",
                Region = "Washington",
                PostalCode = "98101",
                CountryCode = "USA",
                AddressType = "PointAddress"
            },
            Location = new EsriLocation
            {
                X = -122.335167,
                Y = 47.608013
            }
        };

        SetupHttpResponse(esriResponse);

        // Act
        var result = await _provider.ReverseGeocodeAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("123 Main St, Seattle, WA, 98101, USA", result.Address);
        Assert.Equal(-122.335167, result.X, precision: 6);
        Assert.Equal(47.608013, result.Y, precision: 6);
        Assert.Equal("PointAddress", result.AddressType);
        Assert.NotNull(result.StructuredAddress);
        Assert.Equal("Seattle", result.StructuredAddress.City);
        Assert.Equal("Washington", result.StructuredAddress.Region);
    }

    [Fact]
    public async Task SuggestAsync_WithValidResponse_ShouldReturnSuggestions()
    {
        // Arrange
        var request = new SuggestGeocodeRequest("123 Main", MaxResults: 5);
        var esriResponse = new EsriSuggestResponse
        {
            Suggestions = new[]
            {
                new EsriSuggestion
                {
                    Text = "123 Main St, Seattle, WA",
                    MagicKey = "test-magic-key-1",
                    IsCollection = false
                },
                new EsriSuggestion
                {
                    Text = "123 Main Ave, Portland, OR",
                    MagicKey = "test-magic-key-2",
                    IsCollection = false
                }
            }
        };

        SetupHttpResponse(esriResponse);

        // Act
        var result = await _provider.SuggestAsync(request);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("123 Main St, Seattle, WA", result[0].Text);
        Assert.Equal("test-magic-key-1", result[0].MagicKey);
        Assert.False(result[0].IsCollection);
    }

    [Fact]
    public async Task BatchGeocodeAsync_WithValidResponse_ShouldReturnResults()
    {
        // Arrange
        var request = new BatchGeocodeRequest(new[] { "123 Main St", "456 Oak Ave" });
        var esriResponse = new EsriBatchGeocodeResponse
        {
            Locations = new[]
            {
                new EsriBatchLocation
                {
                    Address = "123 Main St, Seattle, WA",
                    Location = new EsriLocation { X = -122.335167, Y = 47.608013 },
                    Score = 95.0,
                    ResultId = "1",
                    Attributes = new Dictionary<string, object?> { ["Addr_type"] = "PointAddress" }
                }
            }
        };

        SetupHttpResponse(esriResponse);

        // Act
        var result = await _provider.BatchGeocodeAsync(request);

        // Assert
        Assert.Single(result);
        var candidate = result[0];
        Assert.Equal("123 Main St, Seattle, WA", candidate.Address);
        Assert.Equal("1", candidate.ProviderId);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithEsriError_ShouldThrowException()
    {
        // Arrange
        var request = new ForwardGeocodeRequest("invalid address");
        var esriResponse = new EsriFindCandidatesResponse
        {
            Error = new EsriError
            {
                Code = 400,
                Message = "Invalid address format",
                Details = new[] { "Address cannot be parsed" }
            }
        };

        SetupHttpResponse(esriResponse);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _provider.ForwardGeocodeAsync(request));

        Assert.Contains("Invalid address format", exception.Message);
        Assert.Contains("Code: 400", exception.Message);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithHttpError_ShouldThrowHttpRequestException()
    {
        // Arrange
        var request = new ForwardGeocodeRequest("test address");

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("Unauthorized")
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => _provider.ForwardGeocodeAsync(request));

        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task ForwardGeocodeAsync_WithUnsupportedSpatialReference_ShouldThrowArgumentException()
    {
        // Arrange
        var request = new ForwardGeocodeRequest("test", SpatialReferenceWkid: 2154); // Unsupported SRID

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _provider.ForwardGeocodeAsync(request));

        Assert.Contains("not supported", exception.Message);
    }

    [Fact]
    public async Task CheckHealthAsync_WithSuccessfulResponse_ShouldReturnHealthy()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var health = await _provider.CheckHealthAsync();

        // Assert
        Assert.True(health.IsHealthy);
        Assert.Equal("esri", health.ProviderName);
        Assert.Null(health.ErrorMessage);
        Assert.True(health.ResponseTimeMs > 0);
    }

    [Fact]
    public async Task CheckHealthAsync_WithHttpError_ShouldReturnUnhealthy()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        // Act
        var health = await _provider.CheckHealthAsync();

        // Assert
        Assert.False(health.IsHealthy);
        Assert.Equal("esri", health.ProviderName);
        Assert.Contains("Service unavailable", health.ErrorMessage);
        Assert.True(health.ResponseTimeMs > 0);
    }

    private void SetupHttpResponse<T>(T responseObject)
    {
        var json = JsonSerializer.Serialize(responseObject, (JsonSerializerOptions?)null);
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
        await _provider.DisposeAsync();
        _httpClient.Dispose();
    }
}