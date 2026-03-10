// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Geocoding.Providers;

/// <summary>
/// Manages OAuth tokens for Esri ArcGIS services
/// </summary>
internal sealed class EsriTokenManager : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly EsriGeocodingOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _tokenSemaphore = new(1, 1);
    private readonly Timer _tokenRefreshTimer;

    private EsriTokenResponse? _currentToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;

    public EsriTokenManager(HttpClient httpClient, EsriGeocodingOptions options, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Set up automatic token refresh timer (refresh 5 minutes before expiration)
        var refreshInterval = TimeSpan.FromMinutes(Math.Max(1, _options.TokenCacheDurationMinutes - 5));
        _tokenRefreshTimer = new Timer(async _ => await RefreshTokenIfNeededAsync().ConfigureAwait(false),
            null, TimeSpan.Zero, refreshInterval);
    }

    /// <summary>
    /// Gets a valid access token, refreshing if necessary
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Valid access token</returns>
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            throw new InvalidOperationException("OAuth ClientId and ClientSecret are required for token-based authentication.");
        }

        // Check if we have a valid token
        if (_currentToken != null && DateTime.UtcNow < _tokenExpiresAt)
        {
            return _currentToken.AccessToken;
        }

        await _tokenSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock
            if (_currentToken != null && DateTime.UtcNow < _tokenExpiresAt)
            {
                return _currentToken.AccessToken;
            }

            return await RequestNewTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenSemaphore.Release();
        }
    }

    private async Task<string> RequestNewTokenAsync(CancellationToken cancellationToken)
    {
        var requestBody = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientId!,
            ["client_secret"] = _options.ClientSecret!,
            ["expiration"] = (_options.TokenCacheDurationMinutes * 60).ToString() // Convert minutes to seconds
        };

        using var content = new FormUrlEncodedContent(requestBody);

        try
        {
            _logger.LogDebug("Requesting new OAuth token from Esri");

            using var response = await _httpClient.PostAsync(_options.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Failed to obtain OAuth token from Esri. Status: {response.StatusCode}, Response: {errorContent}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var tokenResponse = await JsonSerializer.DeserializeAsync<EsriTokenResponse>(
                responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (tokenResponse == null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("Invalid token response from Esri OAuth endpoint.");
            }

            if (tokenResponse.Error != null)
            {
                throw new InvalidOperationException($"OAuth error: {tokenResponse.Error.Message} (Code: {tokenResponse.Error.Code})");
            }

            _currentToken = tokenResponse;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60); // 1 minute buffer

            _logger.LogDebug("Successfully obtained OAuth token, expires at {ExpiresAt}", _tokenExpiresAt);

            return tokenResponse.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to obtain OAuth token from Esri");
            throw;
        }
    }

    private async Task RefreshTokenIfNeededAsync()
    {
        try
        {
            // Only refresh if we're within 5 minutes of expiration
            if (_currentToken != null && DateTime.UtcNow >= _tokenExpiresAt.AddMinutes(-5))
            {
                await GetTokenAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh OAuth token automatically");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _tokenRefreshTimer.DisposeAsync().ConfigureAwait(false);
        _tokenSemaphore.Dispose();
    }
}

/// <summary>
/// Response model for Esri OAuth token requests
/// </summary>
internal sealed class EsriTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("error")]
    public EsriTokenError? Error { get; init; }
}

/// <summary>
/// Error model for Esri OAuth responses
/// </summary>
internal sealed class EsriTokenError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("details")]
    public string[]? Details { get; init; }
}