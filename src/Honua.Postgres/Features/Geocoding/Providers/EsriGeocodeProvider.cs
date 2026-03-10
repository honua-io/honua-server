// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.Geocoding.Providers;

/// <summary>
/// Esri ArcGIS REST geocoding provider implementation
/// </summary>
internal sealed class EsriGeocodeProvider : BaseGeocodeProvider, IAsyncDisposable
{
    public const string ProviderName = "esri";

    private readonly HttpClient _httpClient;
    private readonly EsriGeocodingOptions _options;
    private readonly ILogger<EsriGeocodeProvider> _logger;
    private readonly EsriTokenManager _tokenManager;
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private TokenBucketRateLimiter? _rateLimiter;

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override GeocodeProviderCapabilities Capabilities { get; }

    public EsriGeocodeProvider(
        HttpClient httpClient,
        IOptions<EsriGeocodingOptions> options,
        ILogger<EsriGeocodeProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _tokenManager = new EsriTokenManager(_httpClient, _options, logger);

        // Initialize capabilities based on configuration
        Capabilities = new GeocodeProviderCapabilities(
            SupportsForwardGeocode: true,
            SupportsReverseGeocode: true,
            SupportsSuggest: _options.EnableSuggestions,
            SupportsBatch: _options.EnableBatchGeocoding,
            SupportsStructuredInput: true,
            SupportsBiasing: true)
        {
            SupportedSpatialReferences = [4326, 3857, 102100], // WGS84, Web Mercator
            MaxResultsPerRequest = Math.Min(_options.MaxResults, 50),
            MaxBatchSize = _options.MaxBatchSize,
            RateLimitPerMinute = _options.RateLimitRequestsPerSecond.HasValue
                ? (int)Math.Ceiling(_options.RateLimitRequestsPerSecond.Value * 60)
                : null,
            RequiresAuthentication = !string.IsNullOrWhiteSpace(_options.ApiKey) ||
                                     !string.IsNullOrWhiteSpace(_options.ClientId),
            DefaultTimeoutSeconds = _options.TimeoutSeconds
        };

        // Initialize rate limiter if configured
        if (_options.RateLimitRequestsPerSecond.HasValue)
        {
            var rateLimitOptions = new TokenBucketRateLimiterOptions
            {
                TokenLimit = (int)Math.Ceiling(_options.RateLimitRequestsPerSecond.Value * 10), // 10 second bucket
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                TokensPerPeriod = (int)Math.Ceiling(_options.RateLimitRequestsPerSecond.Value),
                AutoReplenishment = true
            };
            _rateLimiter = new TokenBucketRateLimiter(rateLimitOptions);
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ValidateSpatialReference(request.SpatialReferenceWkid))
        {
            throw new ArgumentException($"Spatial reference {request.SpatialReferenceWkid} is not supported by {Name} provider.");
        }

        await EnforceRateLimitAsync(cancellationToken).ConfigureAwait(false);

        var queryParams = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["f"] = "json",
            ["outSR"] = request.SpatialReferenceWkid.ToString(CultureInfo.InvariantCulture),
            ["maxLocations"] = ValidateMaxResults(request.MaxResults).ToString(CultureInfo.InvariantCulture),
            ["outFields"] = string.Join(",", _options.DefaultOutFields)
        };

        // Handle different input types
        if (request.InputType == GeocodeInputType.Structured && request.StructuredAddress != null)
        {
            BuildStructuredAddressParams(queryParams, request.StructuredAddress);
        }
        else
        {
            queryParams["singleLine"] = request.Query;
        }

        // Add search bounds if provided
        if (request.SearchBounds != null)
        {
            var bounds = request.SearchBounds;
            queryParams["searchExtent"] = $"{bounds.XMin},{bounds.YMin},{bounds.XMax},{bounds.YMax}";
        }

        // Add country codes
        if (!string.IsNullOrWhiteSpace(request.CountryCodes))
        {
            queryParams["countryCode"] = request.CountryCodes;
        }
        else if (_options.DefaultCountries?.Length > 0)
        {
            queryParams["countryCode"] = string.Join(",", _options.DefaultCountries);
        }

        // Add locator if configured
        if (!string.IsNullOrWhiteSpace(_options.DefaultLocator))
        {
            queryParams["locatorName"] = _options.DefaultLocator;
        }

        await AddAuthenticationAsync(queryParams, cancellationToken).ConfigureAwait(false);

        var uri = QueryHelpers.AddQueryString("findAddressCandidates", queryParams);

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessStatusCodeAsync(response).ConfigureAwait(false);

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var esriResponse = await JsonSerializer.DeserializeAsync(
                responseStream,
                EsriJsonContext.Default.EsriFindCandidatesResponse,
                cancellationToken).ConfigureAwait(false);

            if (esriResponse?.Error != null)
            {
                throw new InvalidOperationException($"Esri geocoding error: {esriResponse.Error.Message} (Code: {esriResponse.Error.Code})");
            }

            return MapCandidatesToResults(esriResponse?.Candidates ?? [], request.SpatialReferenceWkid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Forward geocoding failed for query: {Query}", request.Query);
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ValidateSpatialReference(request.SpatialReferenceWkid))
        {
            throw new ArgumentException($"Spatial reference {request.SpatialReferenceWkid} is not supported by {Name} provider.");
        }

        await EnforceRateLimitAsync(cancellationToken).ConfigureAwait(false);

        var queryParams = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["f"] = "json",
            ["location"] = $"{request.X},{request.Y}",
            ["inSR"] = request.SpatialReferenceWkid.ToString(CultureInfo.InvariantCulture),
            ["outSR"] = request.SpatialReferenceWkid.ToString(CultureInfo.InvariantCulture),
            ["outFields"] = string.Join(",", _options.DefaultOutFields)
        };

        // Add distance if provided
        if (request.DistanceMeters.HasValue)
        {
            queryParams["distance"] = request.DistanceMeters.Value.ToString(CultureInfo.InvariantCulture);
        }

        // Add feature types if provided
        if (request.FeatureTypes?.Length > 0)
        {
            queryParams["featureTypes"] = string.Join(",", request.FeatureTypes);
        }

        // Add language if provided
        if (!string.IsNullOrWhiteSpace(request.LanguageCode))
        {
            queryParams["langCode"] = request.LanguageCode;
        }

        await AddAuthenticationAsync(queryParams, cancellationToken).ConfigureAwait(false);

        var uri = QueryHelpers.AddQueryString("reverseGeocode", queryParams);

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessStatusCodeAsync(response).ConfigureAwait(false);

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var esriResponse = await JsonSerializer.DeserializeAsync(
                responseStream,
                EsriJsonContext.Default.EsriReverseGeocodeResponse,
                cancellationToken).ConfigureAwait(false);

            if (esriResponse?.Error != null)
            {
                throw new InvalidOperationException($"Esri reverse geocoding error: {esriResponse.Error.Message} (Code: {esriResponse.Error.Code})");
            }

            return MapReverseGeocodeResult(esriResponse, request.SpatialReferenceWkid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reverse geocoding failed for location: {X},{Y}", request.X, request.Y);
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<GeocodeSuggestion>> SuggestCoreAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnforceRateLimitAsync(cancellationToken).ConfigureAwait(false);

        var queryParams = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["f"] = "json",
            ["text"] = request.Text,
            ["maxSuggestions"] = ValidateMaxResults(request.MaxResults).ToString(CultureInfo.InvariantCulture)
        };

        // Add country codes
        if (!string.IsNullOrWhiteSpace(request.CountryCodes))
        {
            queryParams["countryCode"] = request.CountryCodes;
        }
        else if (_options.DefaultCountries?.Length > 0)
        {
            queryParams["countryCode"] = string.Join(",", _options.DefaultCountries);
        }

        // Add category filter
        if (!string.IsNullOrWhiteSpace(request.CategoryFilter))
        {
            queryParams["category"] = request.CategoryFilter;
        }
        else if (_options.DefaultCategories?.Length > 0)
        {
            queryParams["category"] = string.Join(",", _options.DefaultCategories);
        }

        // Add bias location
        if (request.BiasLocation != null)
        {
            queryParams["location"] = $"{request.BiasLocation.X},{request.BiasLocation.Y}";
        }

        // Add search bounds
        if (request.SearchBounds != null)
        {
            var bounds = request.SearchBounds;
            queryParams["searchExtent"] = $"{bounds.XMin},{bounds.YMin},{bounds.XMax},{bounds.YMax}";
        }

        await AddAuthenticationAsync(queryParams, cancellationToken).ConfigureAwait(false);

        var uri = QueryHelpers.AddQueryString("suggest", queryParams);

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessStatusCodeAsync(response).ConfigureAwait(false);

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var esriResponse = await JsonSerializer.DeserializeAsync(
                responseStream,
                EsriJsonContext.Default.EsriSuggestResponse,
                cancellationToken).ConfigureAwait(false);

            if (esriResponse?.Error != null)
            {
                throw new InvalidOperationException($"Esri suggest error: {esriResponse.Error.Message} (Code: {esriResponse.Error.Code})");
            }

            return MapSuggestionResults(esriResponse?.Suggestions ?? []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Suggestions failed for text: {Text}", request.Text);
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeCoreAsync(
        BatchGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Queries.Count == 0)
        {
            return [];
        }

        if (request.Queries.Count > _options.MaxBatchSize)
        {
            throw new ArgumentException($"Batch size {request.Queries.Count} exceeds maximum allowed size of {_options.MaxBatchSize}.");
        }

        await EnforceRateLimitAsync(cancellationToken).ConfigureAwait(false);

        // Prepare batch request body
        var addresses = new
        {
            records = request.Queries.Select((query, index) => new
            {
                attributes = new
                {
                    OBJECTID = index + 1,
                    SingleLine = query
                }
            }).ToArray()
        };

        var requestBody = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["f"] = "json",
            ["addresses"] = JsonSerializer.Serialize(addresses),
            ["outSR"] = request.SpatialReferenceWkid.ToString(CultureInfo.InvariantCulture),
            ["outFields"] = string.Join(",", _options.DefaultOutFields)
        };

        // Add country codes
        if (!string.IsNullOrWhiteSpace(request.CountryCodes))
        {
            requestBody["countryCode"] = request.CountryCodes;
        }
        else if (_options.DefaultCountries?.Length > 0)
        {
            requestBody["countryCode"] = string.Join(",", _options.DefaultCountries);
        }

        await AddAuthenticationAsync(requestBody, cancellationToken).ConfigureAwait(false);

        var content = new FormUrlEncodedContent(requestBody.Where(kvp => kvp.Value != null).Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value!)));

        try
        {
            using var response = await _httpClient.PostAsync("geocodeAddresses", content, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessStatusCodeAsync(response).ConfigureAwait(false);

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var esriResponse = await JsonSerializer.DeserializeAsync(
                responseStream,
                EsriJsonContext.Default.EsriBatchGeocodeResponse,
                cancellationToken).ConfigureAwait(false);

            if (esriResponse?.Error != null)
            {
                throw new InvalidOperationException($"Esri batch geocoding error: {esriResponse.Error.Message} (Code: {esriResponse.Error.Code})");
            }

            return MapBatchLocationResults(esriResponse?.Locations ?? [], request.SpatialReferenceWkid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Batch geocoding failed for {Count} queries", request.Queries.Count);
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        var queryParams = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["f"] = "json"
        };

        await AddAuthenticationAsync(queryParams, cancellationToken).ConfigureAwait(false);
        var uri = QueryHelpers.AddQueryString("", queryParams);

        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessStatusCodeAsync(response).ConfigureAwait(false);
    }

    private async Task EnforceRateLimitAsync(CancellationToken cancellationToken)
    {
        if (_rateLimiter == null)
        {
            return;
        }

        await _rateLimitSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            if (!lease.IsAcquired)
            {
                throw new InvalidOperationException("Rate limit exceeded for Esri geocoding provider.");
            }
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    private async Task AddAuthenticationAsync(Dictionary<string, string?> queryParams, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            queryParams["token"] = _options.ApiKey;
        }
        else if (!string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            var token = await _tokenManager.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            queryParams["token"] = token;
        }
    }

    private static void BuildStructuredAddressParams(Dictionary<string, string?> queryParams, StructuredAddress address)
    {
        if (!string.IsNullOrWhiteSpace(address.AddressNumber))
            queryParams["address"] = address.AddressNumber;

        if (!string.IsNullOrWhiteSpace(address.StreetName))
            queryParams["address2"] = address.StreetName;

        if (!string.IsNullOrWhiteSpace(address.City))
            queryParams["city"] = address.City;

        if (!string.IsNullOrWhiteSpace(address.Region))
            queryParams["region"] = address.Region;

        if (!string.IsNullOrWhiteSpace(address.PostalCode))
            queryParams["postal"] = address.PostalCode;

        if (!string.IsNullOrWhiteSpace(address.Country))
            queryParams["countryCode"] = address.Country;
    }

    private IReadOnlyList<GeocodeCandidate> MapCandidatesToResults(EsriCandidate[] candidates, int spatialReference)
    {
        var results = new List<GeocodeCandidate>(candidates.Length);

        foreach (var candidate in candidates)
        {
            if (candidate.Location == null || string.IsNullOrWhiteSpace(candidate.Address))
            {
                continue;
            }

            var attributes = BuildAttributes(
                candidate.Attributes?.GetValueOrDefault("USER_FLD")?.ToString(),
                MapEsriAttributes(candidate.Attributes));

            var structuredAddress = MapEsriAddressToStructured(candidate.Attributes);

            var result = new GeocodeCandidate(
                Address: candidate.Address,
                X: candidate.Location.X,
                Y: candidate.Location.Y,
                Score: NormalizeScore(candidate.Score, 0, 100),
                Attributes: attributes,
                ProviderId: candidate.Attributes?.GetValueOrDefault("USER_FLD")?.ToString())
            {
                SpatialReferenceWkid = spatialReference,
                AddressType = candidate.Attributes?.GetValueOrDefault("Addr_type")?.ToString(),
                MatchLevel = GetMatchLevel(candidate.Score),
                StructuredAddress = structuredAddress
            };

            results.Add(result);
        }

        return results;
    }

    private ReverseGeocodeMatch? MapReverseGeocodeResult(EsriReverseGeocodeResponse? response, int spatialReference)
    {
        if (response?.Address == null || response.Location == null)
        {
            return null;
        }

        var addressText = response.Address.LongLabel ?? response.Address.MatchAddress ??
                          response.Address.ShortLabel ?? "Unknown Address";

        var attributes = BuildAttributes(
            response.Address.AddressLine,
            MapEsriAddressAttributes(response.Address));

        var structuredAddress = MapEsriAddressComponentsToStructured(response.Address);

        return new ReverseGeocodeMatch(
            Address: addressText,
            X: response.Location.X,
            Y: response.Location.Y,
            Attributes: attributes,
            ProviderId: response.Address.AddressLine)
        {
            SpatialReferenceWkid = spatialReference,
            AddressType = response.Address.AddressType,
            StructuredAddress = structuredAddress
        };
    }

    private static IReadOnlyList<GeocodeSuggestion> MapSuggestionResults(EsriSuggestion[] suggestions)
    {
        return suggestions
            .Where(s => !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => new GeocodeSuggestion(
                Text: s.Text!,
                MagicKey: s.MagicKey ?? string.Empty,
                IsCollection: s.IsCollection))
            .ToArray();
    }

    private IReadOnlyList<GeocodeCandidate> MapBatchLocationResults(EsriBatchLocation[] locations, int spatialReference)
    {
        var results = new List<GeocodeCandidate>(locations.Length);

        foreach (var location in locations)
        {
            if (location.Location == null || string.IsNullOrWhiteSpace(location.Address))
            {
                continue;
            }

            var attributes = BuildAttributes(
                location.ResultId,
                MapEsriAttributes(location.Attributes));

            var structuredAddress = MapEsriAddressToStructured(location.Attributes);

            var result = new GeocodeCandidate(
                Address: location.Address,
                X: location.Location.X,
                Y: location.Location.Y,
                Score: NormalizeScore(location.Score, 0, 100),
                Attributes: attributes,
                ProviderId: location.ResultId)
            {
                SpatialReferenceWkid = spatialReference,
                AddressType = location.Attributes?.GetValueOrDefault("Addr_type")?.ToString(),
                MatchLevel = GetMatchLevel(location.Score),
                StructuredAddress = structuredAddress
            };

            results.Add(result);
        }

        return results;
    }

    private static IReadOnlyDictionary<string, string?> MapEsriAttributes(Dictionary<string, object?>? esriAttributes)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (esriAttributes == null)
        {
            return attributes;
        }

        foreach (var kvp in esriAttributes)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key))
            {
                attributes[kvp.Key] = kvp.Value?.ToString();
            }
        }

        return attributes;
    }

    private static IReadOnlyDictionary<string, string?> MapEsriAddressAttributes(EsriAddress address)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Address"] = address.AddressLine,
            ["City"] = address.City,
            ["Region"] = address.Region,
            ["RegionAbbr"] = address.RegionAbbr,
            ["Postal"] = address.PostalCode,
            ["PostalExt"] = address.PostalExt,
            ["CountryCode"] = address.CountryCode,
            ["AddNum"] = address.AddressNumber,
            ["StName"] = address.StreetName,
            ["StType"] = address.StreetType,
            ["Subaddress"] = address.Subaddress,
            ["PlaceName"] = address.PlaceName,
            ["Neighborhood"] = address.Neighborhood,
            ["District"] = address.District,
            ["MetroArea"] = address.MetroArea,
            ["LongLabel"] = address.LongLabel,
            ["ShortLabel"] = address.ShortLabel,
            ["Addr_type"] = address.AddressType,
            ["Match_addr"] = address.MatchAddress
        };

        return attributes.Where(kvp => kvp.Value != null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static StructuredAddress? MapEsriAddressToStructured(Dictionary<string, object?>? esriAttributes)
    {
        if (esriAttributes == null)
        {
            return null;
        }

        return new StructuredAddress
        {
            AddressNumber = esriAttributes.GetValueOrDefault("AddNum")?.ToString(),
            StreetName = esriAttributes.GetValueOrDefault("StName")?.ToString(),
            City = esriAttributes.GetValueOrDefault("City")?.ToString(),
            Region = esriAttributes.GetValueOrDefault("Region")?.ToString(),
            PostalCode = esriAttributes.GetValueOrDefault("Postal")?.ToString(),
            Country = esriAttributes.GetValueOrDefault("CountryCode")?.ToString(),
            Subaddress = esriAttributes.GetValueOrDefault("Subaddress")?.ToString(),
            Neighborhood = esriAttributes.GetValueOrDefault("Neighborhood")?.ToString()
        };
    }

    private static StructuredAddress? MapEsriAddressComponentsToStructured(EsriAddress address)
    {
        return new StructuredAddress
        {
            AddressNumber = address.AddressNumber,
            StreetName = address.StreetName,
            City = address.City,
            Region = address.Region,
            PostalCode = address.PostalCode,
            Country = address.CountryCode,
            Subaddress = address.Subaddress,
            Neighborhood = address.Neighborhood
        };
    }

    private static string? GetMatchLevel(double score)
    {
        return score switch
        {
            >= 95 => "exact",
            >= 85 => "interpolated",
            >= 75 => "approximate",
            _ => "poor"
        };
    }

    private static async Task EnsureSuccessStatusCodeAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException(
                $"Esri geocoding request failed with status code {(int)response.StatusCode}. Response: {errorContent}");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _tokenManager.DisposeAsync().ConfigureAwait(false);
        _rateLimiter?.Dispose();
        _rateLimitSemaphore.Dispose();
    }
}