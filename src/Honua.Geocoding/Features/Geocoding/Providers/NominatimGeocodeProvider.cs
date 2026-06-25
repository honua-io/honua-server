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
/// Nominatim geocoding provider using OpenStreetMap data
/// </summary>
internal sealed class NominatimGeocodeProvider : BaseGeocodeProvider
{
    /// <summary>
    /// Nominatim provider name constant
    /// </summary>
    public const string ProviderName = "nominatim";

    private readonly NominatimProviderConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private int _validatedBaseUrl;

    /// <summary>
    /// Initialize a new Nominatim geocode provider
    /// </summary>
    /// <param name="configuration">Provider configuration</param>
    /// <param name="httpClient">HTTP client for API calls</param>
    public NominatimGeocodeProvider(
        NominatimProviderConfiguration configuration,
        HttpClient httpClient)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        // The injected HttpClient may be a shared/pooled typed client. Mutating its Timeout or
        // DefaultRequestHeaders here would throw once a request has started and would bleed
        // configuration across scopes, so per-request User-Agent/contact headers and timeout are
        // applied on each outbound request instead (see SendAsync).
    }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override GeocodeProviderCapabilities Capabilities => new(
        SupportsForwardGeocode: true,
        SupportsReverseGeocode: true,
        SupportsSuggest: _configuration.EnableSuggestFromSearch,
        // Nominatim has no native batch endpoint, but BaseGeocodeProvider fans the batch out
        // to sequential forward-geocode calls so the Esri geocodeAddresses operation works.
        SupportsBatch: true,
        SupportsStructuredInput: true,
        SupportsBiasing: true)
    {
        SupportedSpatialReferences = [4326], // Nominatim returns WGS84 coordinates
        MaxResultsPerRequest = 50,
        // Kept conservative because batch is fanned out to sequential single-address requests
        // against the public Nominatim usage policy (max ~1 req/sec).
        MaxBatchSize = _configuration.MaxBatchSize,
        RequiresAuthentication = false,
        SupportedLanguages = ["en", "de", "fr", "es", "it", "pt", "ru", "zh", "ja"],
        DefaultTimeoutSeconds = _configuration.TimeoutSeconds
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
                throw new GeocodeProviderException($"Nominatim API returned {response.StatusCode}: {response.ReasonPhrase}")
                {
                    ProviderName = Name,
                    ErrorCode = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? GeocodeErrorCodes.RateLimitExceeded
                        : GeocodeErrorCodes.ServiceUnavailable
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var searchResults = JsonSerializer.Deserialize(
                content,
                NominatimProviderJsonContext.Default.NominatimSearchResultArray);

            return ConvertSearchResults(searchResults ?? [], request);
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeProviderException($"Failed to connect to Nominatim service: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
        catch (TaskCanceledException ex)
        {
            throw new GeocodeProviderException($"Nominatim request timed out: {ex.Message}", ex)
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
                throw new GeocodeProviderException($"Nominatim API returned {response.StatusCode}: {response.ReasonPhrase}")
                {
                    ProviderName = Name,
                    ErrorCode = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? GeocodeErrorCodes.RateLimitExceeded
                        : GeocodeErrorCodes.ServiceUnavailable
                };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var reverseResult = JsonSerializer.Deserialize(
                content,
                NominatimProviderJsonContext.Default.NominatimReverseResult);

            return ConvertReverseResult(reverseResult, request);
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeProviderException($"Failed to connect to Nominatim service: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
        catch (TaskCanceledException ex)
        {
            throw new GeocodeProviderException($"Nominatim request timed out: {ex.Message}", ex)
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

        // Use search endpoint with limited results for suggestions
        var searchRequest = new ForwardGeocodeRequest(
            Query: request.Text,
            MaxResults: Math.Min(request.MaxResults, _configuration.MaxSuggestions),
            CountryCodes: request.CountryCodes);

        var searchResults = await ForwardGeocodeAsync(searchRequest, cancellationToken).ConfigureAwait(false);

        return searchResults.Select((result, index) => new GeocodeSuggestion(
            Text: result.Address,
            MagicKey: $"nominatim_{result.ProviderId}",
            IsCollection: false)
        {
            Category = result.AddressType
        }).ToArray();
    }

    /// <inheritdoc />
    protected override async Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var url = $"{_configuration.BaseUrl.TrimEnd('/')}/status.php?format=json";
            using var response = await SendGetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GeocodeProviderException($"Nominatim health check failed: {response.StatusCode}")
                {
                    ProviderName = Name,
                    ErrorCode = GeocodeErrorCodes.ServiceUnavailable
                };
            }
        }
        catch (HttpRequestException ex)
        {
            throw new GeocodeProviderException($"Nominatim health check failed: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.NetworkTimeout
            };
        }
    }

    /// <summary>
    /// Issues a GET against the injected (potentially shared) HttpClient, applying the configured
    /// User-Agent, optional contact email header, and request timeout per request rather than
    /// mutating the shared client. The caller owns disposal of the returned response.
    /// </summary>
    private async Task<HttpResponseMessage> SendGetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(_configuration.UserAgent);

        if (!string.IsNullOrWhiteSpace(_configuration.Email))
        {
            request.Headers.Add("X-Contact-Email", _configuration.Email);
        }

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
                $"Nominatim BaseUrl {validation.ErrorMessage ?? "must be a valid HTTPS URL."}")
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

        var url = $"{baseUrl}/search?format=json&addressdetails=1&limit={maxResults}&q={Uri.EscapeDataString(request.Query)}";

        if (!string.IsNullOrWhiteSpace(request.CountryCodes))
        {
            url += $"&countrycodes={Uri.EscapeDataString(request.CountryCodes)}";
        }

        if (request.SearchBounds != null)
        {
            url += "&viewbox=" +
                   request.SearchBounds.XMin.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.YMax.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.XMax.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   request.SearchBounds.YMin.ToString("F6", CultureInfo.InvariantCulture);
            url += "&bounded=1";
        }
        else if (request.BiasLocation != null)
        {
            // Nominatim has no dedicated proximity parameter, so a proximity bias is expressed as
            // an UNbounded viewbox centred on the bias point: results inside it are preferred but
            // results outside are still returned (omitting bounded=1). The half-extent is derived
            // from BiasDistanceMeters when supplied, otherwise a small default radius (#2148).
            var radiusMeters = request.BiasDistanceMeters is > 0 ? request.BiasDistanceMeters.Value : DefaultBiasRadiusMeters;
            var latHalf = radiusMeters / MetersPerDegreeLatitude;
            var cosLat = Math.Cos(request.BiasLocation.Y * Math.PI / 180.0);
            var lonHalf = latHalf / Math.Max(Math.Abs(cosLat), 1e-6);

            var xmin = request.BiasLocation.X - lonHalf;
            var xmax = request.BiasLocation.X + lonHalf;
            var ymin = request.BiasLocation.Y - latHalf;
            var ymax = request.BiasLocation.Y + latHalf;

            url += "&viewbox=" +
                   xmin.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   ymax.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   xmax.ToString("F6", CultureInfo.InvariantCulture) + "," +
                   ymin.ToString("F6", CultureInfo.InvariantCulture);
        }

        return url;
    }

    // Default proximity-bias half-extent (meters) used when a forward request supplies a bias
    // location but no explicit distance. Sized so the unbounded viewbox covers a city-scale area.
    private const double DefaultBiasRadiusMeters = 25_000.0;

    // Approximate meters per degree of latitude (constant; longitude is scaled by cos(latitude)).
    private const double MetersPerDegreeLatitude = 111_320.0;

    private string BuildReverseUrl(ReverseGeocodeRequest request)
    {
        var baseUrl = _configuration.BaseUrl.TrimEnd('/');

        var url = $"{baseUrl}/reverse?format=json&addressdetails=1" +
                  $"&lat={request.Y.ToString("F6", CultureInfo.InvariantCulture)}" +
                  $"&lon={request.X.ToString("F6", CultureInfo.InvariantCulture)}";

        if (!string.IsNullOrWhiteSpace(request.LanguageCode))
        {
            url += $"&accept-language={Uri.EscapeDataString(request.LanguageCode)}";
        }

        return url;
    }

    private List<GeocodeCandidate> ConvertSearchResults(
        NominatimSearchResult[] results,
        ForwardGeocodeRequest request)
    {
        var candidates = new List<GeocodeCandidate>();

        for (int i = 0; i < results.Length; i++)
        {
            var result = results[i];

            var attributes = BuildAttributes(
                providerId: result.PlaceId?.ToString(CultureInfo.InvariantCulture),
                additionalAttributes: new Dictionary<string, string?>
                {
                    ["osm_type"] = result.OsmType,
                    ["osm_id"] = result.OsmId?.ToString(CultureInfo.InvariantCulture),
                    ["class"] = result.Class,
                    ["type"] = result.Type,
                    ["importance"] = result.Importance?.ToString("F3", CultureInfo.InvariantCulture),
                    ["display_name"] = result.DisplayName,
                    ["licence"] = result.Licence
                });

            var score = CalculateSearchScore(result, i);
            var structuredAddress = ConvertAddress(result.Address);

            candidates.Add(new GeocodeCandidate(
                Address: result.DisplayName ?? "Unknown Address",
                X: result.Lon,
                Y: result.Lat,
                Score: score,
                Attributes: attributes,
                ProviderId: result.PlaceId?.ToString(CultureInfo.InvariantCulture))
            {
                MatchLevel = score > 90 ? "exact" : score > 70 ? "interpolated" : "approximate",
                AddressType = GetAddressType(result.Type),
                SpatialReferenceWkid = request.SpatialReferenceWkid,
                StructuredAddress = structuredAddress
            });
        }

        return candidates;
    }

    private ReverseGeocodeMatch? ConvertReverseResult(
        NominatimReverseResult? result,
        ReverseGeocodeRequest request)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.DisplayName))
        {
            return null;
        }

        var attributes = BuildAttributes(
            providerId: result.PlaceId?.ToString(CultureInfo.InvariantCulture),
            additionalAttributes: new Dictionary<string, string?>
            {
                ["osm_type"] = result.OsmType,
                ["osm_id"] = result.OsmId?.ToString(CultureInfo.InvariantCulture),
                ["display_name"] = result.DisplayName,
                ["licence"] = result.Licence
            });

        var structuredAddress = ConvertAddress(result.Address);

        return new ReverseGeocodeMatch(
            Address: result.DisplayName,
            X: request.X,
            Y: request.Y,
            Attributes: attributes,
            ProviderId: result.PlaceId?.ToString(CultureInfo.InvariantCulture))
        {
            AddressType = GetAddressType(result.AddressType),
            SpatialReferenceWkid = request.SpatialReferenceWkid,
            StructuredAddress = structuredAddress
        };
    }

    private static double CalculateSearchScore(NominatimSearchResult result, int resultIndex)
    {
        // Start with base score that decreases with position
        var baseScore = Math.Max(100 - (resultIndex * 5), 20);

        // Adjust based on importance if available
        if (result.Importance.HasValue)
        {
            baseScore = (int)(baseScore * (0.3 + (result.Importance.Value * 0.7)));
        }

        return Math.Clamp(baseScore, 0, 100);
    }

    private static string? GetAddressType(string? osmType)
    {
        return osmType switch
        {
            "house" => "PointAddress",
            "building" => "PointAddress",
            "road" => "StreetAddress",
            "neighbourhood" => "Neighborhood",
            "suburb" => "Subregion",
            "city" => "City",
            "town" => "City",
            "village" => "City",
            "county" => "County",
            "state" => "State",
            "country" => "Country",
            "postcode" => "PostalCode",
            _ => "Locality"
        };
    }

    private static StructuredAddress? ConvertAddress(NominatimAddress? address)
    {
        if (address == null) return null;

        return new StructuredAddress
        {
            AddressNumber = address.HouseNumber,
            StreetName = address.Road,
            City = address.City ?? address.Town ?? address.Village,
            Region = address.State,
            PostalCode = address.Postcode,
            Country = address.Country,
            Neighborhood = address.Neighbourhood ?? address.Suburb
        };
    }

    // Nominatim API response models
    internal sealed class NominatimSearchResult
    {
        [JsonPropertyName("place_id")]
        public long? PlaceId { get; set; }

        [JsonPropertyName("licence")]
        public string? Licence { get; set; }

        [JsonPropertyName("osm_type")]
        public string? OsmType { get; set; }

        [JsonPropertyName("osm_id")]
        public long? OsmId { get; set; }

        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("class")]
        public string? Class { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("importance")]
        public double? Importance { get; set; }

        [JsonPropertyName("address")]
        public NominatimAddress? Address { get; set; }
    }

    internal sealed class NominatimReverseResult
    {
        [JsonPropertyName("place_id")]
        public long? PlaceId { get; set; }

        [JsonPropertyName("licence")]
        public string? Licence { get; set; }

        [JsonPropertyName("osm_type")]
        public string? OsmType { get; set; }

        [JsonPropertyName("osm_id")]
        public long? OsmId { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("address")]
        public NominatimAddress? Address { get; set; }

        [JsonPropertyName("addresstype")]
        public string? AddressType { get; set; }
    }

    internal sealed class NominatimAddress
    {
        [JsonPropertyName("house_number")]
        public string? HouseNumber { get; set; }

        [JsonPropertyName("road")]
        public string? Road { get; set; }

        [JsonPropertyName("neighbourhood")]
        public string? Neighbourhood { get; set; }

        [JsonPropertyName("suburb")]
        public string? Suburb { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("town")]
        public string? Town { get; set; }

        [JsonPropertyName("village")]
        public string? Village { get; set; }

        [JsonPropertyName("county")]
        public string? County { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("postcode")]
        public string? Postcode { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(NominatimGeocodeProvider.NominatimSearchResult[]))]
[JsonSerializable(typeof(NominatimGeocodeProvider.NominatimReverseResult))]
[JsonSerializable(typeof(NominatimGeocodeProvider.NominatimAddress))]
internal sealed partial class NominatimProviderJsonContext : JsonSerializerContext;
