// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.Geocoding.Features.Geocoding.Providers;

/// <summary>
/// Azure Maps geocoding provider
/// </summary>
internal sealed class AzureMapsGeocodeProvider : BaseGeocodeProvider
{
    /// <summary>
    /// Azure Maps provider name constant
    /// </summary>
    public const string ProviderName = "azure-maps";

    private readonly AzureMapsProviderConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private int _validatedBaseUrl;

    /// <summary>
    /// Initialize a new Azure Maps geocode provider
    /// </summary>
    /// <param name="configuration">Provider configuration</param>
    /// <param name="httpClient">HTTP client for API calls</param>
    public AzureMapsGeocodeProvider(
        AzureMapsProviderConfiguration configuration,
        HttpClient httpClient)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        if (string.IsNullOrWhiteSpace(configuration.SubscriptionKey))
        {
            throw new GeocodeProviderException("Azure Maps subscription key is required")
            {
                ProviderName = ProviderName,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }

        // The injected HttpClient may be a shared/pooled typed client. Mutating its Timeout or
        // DefaultRequestHeaders here would throw once a request has started and would bleed the
        // subscription key across scopes, so the subscription-key header and request timeout are
        // applied on each outbound request instead (see SendGetAsync).
    }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override GeocodeProviderCapabilities Capabilities => new(
        SupportsForwardGeocode: true,
        SupportsReverseGeocode: true,
        SupportsSuggest: true,
        SupportsBatch: false, // Azure Maps doesn't have native batch support in Search API
        SupportsStructuredInput: false,
        SupportsBiasing: true)
    {
        SupportedSpatialReferences = [4326], // Azure Maps returns WGS84 coordinates
        MaxResultsPerRequest = 100,
        MaxBatchSize = 0,
        RequiresAuthentication = true,
        SupportedLanguages = ["en", "ar", "cs", "da", "de", "el", "es", "et", "fi", "fr", "he", "hu", "it", "ja", "ko", "lv", "lt", "nb", "nl", "pl", "pt", "ru", "sk", "sl", "sv", "th", "tr", "uk", "zh"],
        DefaultTimeoutSeconds = _configuration.TimeoutSeconds,
        RateLimitPerMinute = 500 // Azure Maps default limit
    };

    /// <inheritdoc />
    public override async Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return [];
        }

        if (!ValidateSpatialReference(request.SpatialReferenceWkid))
        {
            throw new GeocodeRequestException($"Unsupported spatial reference: {request.SpatialReferenceWkid}")
            {
                ParameterName = nameof(request.SpatialReferenceWkid)
            };
        }

        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var url = BuildSearchUrl(request);
            using var response = await SendGetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GeocodeProviderException($"Azure Maps API returned {response.StatusCode}: {response.ReasonPhrase}")
                {
                    ProviderName = Name,
                    ErrorCode = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.TooManyRequests => GeocodeErrorCodes.RateLimitExceeded,
                        System.Net.HttpStatusCode.Unauthorized => GeocodeErrorCodes.AuthenticationFailed,
                        System.Net.HttpStatusCode.Forbidden => GeocodeErrorCodes.AuthenticationFailed,
                        _ => GeocodeErrorCodes.ServiceUnavailable
                    }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var searchResponse = JsonSerializer.Deserialize(
                content,
                AzureMapsProviderJsonContext.Default.AzureMapsSearchResponse);

            return ConvertSearchResults(searchResponse?.Results ?? [], request);
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeProviderException($"Failed to connect to Azure Maps service: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
        catch (TaskCanceledException ex)
        {
            throw new GeocodeProviderException($"Azure Maps request timed out: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
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
            throw new GeocodeRequestException($"Unsupported spatial reference: {request.SpatialReferenceWkid}")
            {
                ParameterName = nameof(request.SpatialReferenceWkid)
            };
        }

        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var url = BuildReverseUrl(request);
            using var response = await SendGetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GeocodeProviderException($"Azure Maps API returned {response.StatusCode}: {response.ReasonPhrase}")
                {
                    ProviderName = Name,
                    ErrorCode = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.TooManyRequests => GeocodeErrorCodes.RateLimitExceeded,
                        System.Net.HttpStatusCode.Unauthorized => GeocodeErrorCodes.AuthenticationFailed,
                        System.Net.HttpStatusCode.Forbidden => GeocodeErrorCodes.AuthenticationFailed,
                        _ => GeocodeErrorCodes.ServiceUnavailable
                    }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var reverseResponse = JsonSerializer.Deserialize(
                content,
                AzureMapsProviderJsonContext.Default.AzureMapsReverseResponse);

            return ConvertReverseResult(reverseResponse?.Addresses?.FirstOrDefault(), request);
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeProviderException($"Failed to connect to Azure Maps service: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
        catch (TaskCanceledException ex)
        {
            throw new GeocodeProviderException($"Azure Maps request timed out: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<GeocodeSuggestion>> SuggestCoreAsync(
        SuggestGeocodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return [];
        }

        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var url = BuildSuggestUrl(request);
            using var response = await SendGetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GeocodeProviderException($"Azure Maps API returned {response.StatusCode}: {response.ReasonPhrase}")
                {
                    ProviderName = Name,
                    ErrorCode = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.TooManyRequests => GeocodeErrorCodes.RateLimitExceeded,
                        System.Net.HttpStatusCode.Unauthorized => GeocodeErrorCodes.AuthenticationFailed,
                        System.Net.HttpStatusCode.Forbidden => GeocodeErrorCodes.AuthenticationFailed,
                        _ => GeocodeErrorCodes.ServiceUnavailable
                    }
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var suggestionResponse = JsonSerializer.Deserialize(
                content,
                AzureMapsProviderJsonContext.Default.AzureMapsSuggestionResponse);

            return ConvertSuggestionResults(suggestionResponse?.Results ?? []);
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeProviderException($"Failed to connect to Azure Maps service: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
        catch (TaskCanceledException ex)
        {
            throw new GeocodeProviderException($"Azure Maps request timed out: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
    }

    /// <inheritdoc />
    protected override async Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Simple health check using a known address
            var url = $"{_configuration.BaseUrl.TrimEnd('/')}/search/address/json?" +
                      $"api-version={_configuration.ApiVersion}" +
                      "&query=1%20Microsoft%20Way%2C%20Redmond%2C%20WA" +
                      "&limit=1";

            using var response = await SendGetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GeocodeProviderException($"Azure Maps health check failed: {response.StatusCode}")
                {
                    ProviderName = Name,
                    ErrorCode = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized => GeocodeErrorCodes.AuthenticationFailed,
                        System.Net.HttpStatusCode.Forbidden => GeocodeErrorCodes.AuthenticationFailed,
                        _ => GeocodeErrorCodes.ServiceUnavailable
                    }
                };
            }
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeProviderException($"Azure Maps health check failed: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
    }

    /// <summary>
    /// Issues a GET against the injected (potentially shared) HttpClient, applying the Azure Maps
    /// subscription-key header and request timeout per request rather than mutating the shared
    /// client. The key is sent as a header so it is not captured by URL request-logging. The
    /// caller owns disposal of the returned response.
    /// </summary>
    private async Task<HttpResponseMessage> SendGetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("subscription-key", _configuration.SubscriptionKey);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_configuration.TimeoutSeconds));

        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
    }

    private async Task EnsureSafeBaseUrlAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _validatedBaseUrl) == 1)
        {
            return;
        }

        var validation = await OutboundHttpUrlValidator
            .ValidateAsync(_configuration.BaseUrl, cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new GeocodeProviderException(
                $"Azure Maps BaseUrl {validation.ErrorMessage ?? "must be a valid HTTPS URL."}")
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }

        Volatile.Write(ref _validatedBaseUrl, 1);
    }

    private string BuildSearchUrl(ForwardGeocodeRequest request)
    {
        var baseUrl = _configuration.BaseUrl.TrimEnd('/');
        var maxResults = ValidateMaxResults(request.MaxResults);

        var url = $"{baseUrl}/search/address/json?" +
                  $"api-version={_configuration.ApiVersion}" +
                  $"&query={Uri.EscapeDataString(request.Query)}" +
                  $"&limit={maxResults}";

        if (!string.IsNullOrWhiteSpace(_configuration.Language))
        {
            url += $"&language={Uri.EscapeDataString(_configuration.Language)}";
        }

        if (!string.IsNullOrWhiteSpace(_configuration.View))
        {
            url += $"&view={Uri.EscapeDataString(_configuration.View)}";
        }

        if (!string.IsNullOrWhiteSpace(request.CountryCodes))
        {
            url += $"&countrySet={Uri.EscapeDataString(request.CountryCodes)}";
        }

        if (request.SearchBounds != null)
        {
            url += "&bbox=" +
                   request.SearchBounds.XMin.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.YMin.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.XMax.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.YMax.ToString("F6", CultureInfo.InvariantCulture);
        }

        return url;
    }

    private string BuildReverseUrl(ReverseGeocodeRequest request)
    {
        var baseUrl = _configuration.BaseUrl.TrimEnd('/');

        var url = $"{baseUrl}/search/address/reverse/json?" +
                  $"api-version={_configuration.ApiVersion}" +
                  $"&query={request.Y.ToString("F6", CultureInfo.InvariantCulture)},{request.X.ToString("F6", CultureInfo.InvariantCulture)}";

        if (!string.IsNullOrWhiteSpace(_configuration.Language))
        {
            url += $"&language={Uri.EscapeDataString(_configuration.Language)}";
        }

        if (!string.IsNullOrWhiteSpace(_configuration.View))
        {
            url += $"&view={Uri.EscapeDataString(_configuration.View)}";
        }

        // Esri reverseGeocode `distance` maps to the Azure reverse `radius` (meters):
        // both bound the search to a circle around the query point.
        if (request.DistanceMeters is > 0 and var radius)
        {
            url += $"&radius={radius.ToString("F0", CultureInfo.InvariantCulture)}";
        }

        // Esri reverseGeocode `featureTypes` maps to the Azure reverse `entityType`
        // filter, which restricts the geography level of the returned match.
        if (request.FeatureTypes is { Length: > 0 } featureTypes)
        {
            var entityTypes = string.Join(',', featureTypes.Select(MapFeatureTypeToEntityType));
            if (!string.IsNullOrWhiteSpace(entityTypes))
            {
                url += $"&entityType={Uri.EscapeDataString(entityTypes)}";
            }
        }

        return url;
    }

    // Maps Esri feature-type tokens to the Azure Maps reverse `entityType` values.
    // Tokens Azure does not model are passed through unchanged so callers using
    // native Azure entity types continue to work.
    private static string MapFeatureTypeToEntityType(string featureType)
    {
        return featureType switch
        {
            "PointAddress" => "Address",
            "StreetAddress" => "Address",
            "StreetInt" => "Address",
            "Locality" => "Municipality",
            "City" => "Municipality",
            "Neighborhood" => "Neighbourhood",
            "Subregion" => "MunicipalitySubdivision",
            "Region" => "CountrySecondarySubdivision",
            "State" => "CountrySubdivision",
            "Country" => "Country",
            "PostalCode" => "PostalCodeArea",
            _ => featureType
        };
    }

    private string BuildSuggestUrl(SuggestGeocodeRequest request)
    {
        var baseUrl = _configuration.BaseUrl.TrimEnd('/');
        var maxResults = ValidateMaxResults(request.MaxResults);

        var url = $"{baseUrl}/search/address/json?" +
                  $"api-version={_configuration.ApiVersion}" +
                  $"&query={Uri.EscapeDataString(request.Text)}" +
                  $"&limit={maxResults}" +
                  "&typeahead=true";

        if (!string.IsNullOrWhiteSpace(_configuration.Language))
        {
            url += $"&language={Uri.EscapeDataString(_configuration.Language)}";
        }

        if (!string.IsNullOrWhiteSpace(_configuration.View))
        {
            url += $"&view={Uri.EscapeDataString(_configuration.View)}";
        }

        if (!string.IsNullOrWhiteSpace(request.CountryCodes))
        {
            url += $"&countrySet={Uri.EscapeDataString(request.CountryCodes)}";
        }

        if (request.BiasLocation != null)
        {
            url += "&lat=" + request.BiasLocation.Y.ToString("F6", CultureInfo.InvariantCulture) +
                   "&lon=" + request.BiasLocation.X.ToString("F6", CultureInfo.InvariantCulture);
        }

        if (request.SearchBounds != null)
        {
            url += "&bbox=" +
                   request.SearchBounds.XMin.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.YMin.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.XMax.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.YMax.ToString("F6", CultureInfo.InvariantCulture);
        }

        return url;
    }

    private List<GeocodeCandidate> ConvertSearchResults(
        AzureMapsSearchResult[] results,
        ForwardGeocodeRequest request)
    {
        var candidates = new List<GeocodeCandidate>();

        for (int i = 0; i < results.Length; i++)
        {
            var result = results[i];

            var attributes = BuildAttributes(
                providerId: result.Id,
                additionalAttributes: new Dictionary<string, string?>
                {
                    ["score"] = result.Score?.ToString("F3", CultureInfo.InvariantCulture),
                    ["distance"] = result.Dist?.ToString("F0", CultureInfo.InvariantCulture),
                    ["entity_type"] = result.EntityType,
                    ["data_sources"] = result.DataSources?.QueryType,
                    ["match_code"] = result.MatchCode
                });

            var score = CalculateSearchScore(result, i);
            var structuredAddress = ConvertAddress(result.Address);

            candidates.Add(new GeocodeCandidate(
                Address: result.Address?.FreeformAddress ?? "Unknown Address",
                X: result.Position?.Lon ?? 0,
                Y: result.Position?.Lat ?? 0,
                Score: score,
                Attributes: attributes,
                ProviderId: result.Id,
                DistanceMeters: result.Dist)
            {
                MatchLevel = GetMatchLevel(result.MatchCode),
                AddressType = GetAddressType(result.Type, result.EntityType),
                SpatialReferenceWkid = request.SpatialReferenceWkid,
                StructuredAddress = structuredAddress
            });
        }

        return candidates;
    }

    private ReverseGeocodeMatch? ConvertReverseResult(
        AzureMapsReverseAddress? result,
        ReverseGeocodeRequest request)
    {
        if (result?.Address == null)
        {
            return null;
        }

        var attributes = BuildAttributes(
            providerId: result.Id,
            additionalAttributes: new Dictionary<string, string?>
            {
                ["match_code"] = result.MatchCode
            });

        var structuredAddress = ConvertAddress(result.Address);

        return new ReverseGeocodeMatch(
            Address: result.Address.FreeformAddress ?? "Unknown Address",
            X: request.X,
            Y: request.Y,
            Attributes: attributes,
            ProviderId: result.Id)
        {
            AddressType = GetAddressType(result.Type, result.EntityType),
            SpatialReferenceWkid = request.SpatialReferenceWkid,
            StructuredAddress = structuredAddress
        };
    }

    private static List<GeocodeSuggestion> ConvertSuggestionResults(AzureMapsSearchResult[] results)
    {
        var suggestions = new List<GeocodeSuggestion>();

        for (int i = 0; i < results.Length; i++)
        {
            var result = results[i];

            suggestions.Add(new GeocodeSuggestion(
                Text: result.Address?.FreeformAddress ?? result.Poi?.Name ?? "Unknown",
                MagicKey: result.Id ?? $"azure_suggestion_{i}",
                IsCollection: result.Type?.Contains("Geography") == true)
            {
                Category = result.Type
            });
        }

        return suggestions;
    }

    private static double CalculateSearchScore(AzureMapsSearchResult result, int resultIndex)
    {
        // Start with base score that decreases with position
        var baseScore = Math.Max(100 - (resultIndex * 2), 20);

        // Adjust based on Azure Maps score if available
        if (result.Score.HasValue)
        {
            baseScore = (int)(baseScore * result.Score.Value);
        }

        return Math.Clamp(baseScore, 0, 100);
    }

    private static string? GetMatchLevel(string? matchCode)
    {
        return matchCode switch
        {
            "Exact" => "exact",
            "Good" => "exact",
            "Ambiguous" => "approximate",
            "UpHierarchy" => "approximate",
            _ => "interpolated"
        };
    }

    private static string? GetAddressType(string? type, string? entityType)
    {
        if (!string.IsNullOrEmpty(entityType))
        {
            return entityType switch
            {
                "Position" => "PointAddress",
                "RoadBlock" => "StreetAddress",
                "RoadIntersection" => "StreetAddress",
                "Address" => "PointAddress",
                "Neighborhood" => "Neighborhood",
                "PopulatedPlace" => "City",
                "Postcode1" => "PostalCode",
                "AdminDivision1" => "State",
                "AdminDivision2" => "County",
                "CountryRegion" => "Country",
                _ => "Locality"
            };
        }

        if (!string.IsNullOrEmpty(type))
        {
            return type switch
            {
                "Point Address" => "PointAddress",
                "Address Range" => "StreetAddress",
                "Street" => "StreetAddress",
                "Cross Street" => "StreetAddress",
                "Geography" => "Locality",
                _ => "Locality"
            };
        }

        return "Locality";
    }

    private static StructuredAddress? ConvertAddress(AzureMapsAddress? address)
    {
        if (address == null) return null;

        return new StructuredAddress
        {
            AddressNumber = address.StreetNumber,
            StreetName = address.StreetName,
            City = address.Municipality,
            Region = address.CountrySubdivision,
            PostalCode = address.PostalCode,
            Country = address.Country,
            Neighborhood = address.MunicipalitySubdivision
        };
    }

    // Azure Maps API response models
    internal sealed class AzureMapsSearchResponse
    {
        public AzureMapsSearchResult[]? Results { get; set; }
    }

    internal sealed class AzureMapsReverseResponse
    {
        public AzureMapsReverseAddress[]? Addresses { get; set; }
    }

    internal sealed class AzureMapsSuggestionResponse
    {
        public AzureMapsSearchResult[]? Results { get; set; }
    }

    internal sealed class AzureMapsSearchResult
    {
        public string? Type { get; set; }
        public string? Id { get; set; }
        public double? Score { get; set; }
        public double? Dist { get; set; }
        public string? EntityType { get; set; }
        public AzureMapsPosition? Position { get; set; }
        public AzureMapsAddress? Address { get; set; }
        public AzureMapsPoi? Poi { get; set; }
        public AzureMapsDataSources? DataSources { get; set; }
        public string? MatchCode { get; set; }
    }

    internal sealed class AzureMapsReverseAddress
    {
        public string? Type { get; set; }
        public string? Id { get; set; }
        public string? EntityType { get; set; }
        public AzureMapsAddress? Address { get; set; }
        public string? MatchCode { get; set; }
    }

    internal sealed class AzureMapsPosition
    {
        public double? Lat { get; set; }
        public double? Lon { get; set; }
    }

    internal sealed class AzureMapsAddress
    {
        public string? StreetNumber { get; set; }
        public string? StreetName { get; set; }
        public string? MunicipalitySubdivision { get; set; }
        public string? Municipality { get; set; }
        public string? CountrySubdivision { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; }
        public string? CountryCode { get; set; }
        public string? FreeformAddress { get; set; }
    }

    internal sealed class AzureMapsPoi
    {
        public string? Name { get; set; }
        public string? CategorySet { get; set; }
        public string? Categories { get; set; }
    }

    internal sealed class AzureMapsDataSources
    {
        public string? QueryType { get; set; }
        public string? GeometryType { get; set; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AzureMapsGeocodeProvider.AzureMapsSearchResponse))]
[JsonSerializable(typeof(AzureMapsGeocodeProvider.AzureMapsReverseResponse))]
[JsonSerializable(typeof(AzureMapsGeocodeProvider.AzureMapsSuggestionResponse))]
internal sealed partial class AzureMapsProviderJsonContext : JsonSerializerContext;
