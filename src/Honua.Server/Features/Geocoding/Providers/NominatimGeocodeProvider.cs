// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geocoding.Providers;

internal sealed class NominatimGeocodeProvider(
    HttpClient httpClient,
    IOptions<GeocodingOptions> options,
    ILogger<NominatimGeocodeProvider> logger) : IGeocodeProvider
{
    public const string ProviderName = "nominatim";

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly GeocodingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<NominatimGeocodeProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private int _validatedBaseUrl;

    public string Name => ProviderName;

    public GeocodeProviderCapabilities Capabilities => new(
        SupportsSuggest: _options.Nominatim.EnableSuggestFromSearch,
        SupportsBatch: false,
        SupportsStructuredInput: false,
        SupportsBiasing: true);

    public async Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(ForwardGeocodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["format"] = "jsonv2",
            ["addressdetails"] = "1",
            ["q"] = request.Query,
            ["limit"] = request.MaxResults.ToString(CultureInfo.InvariantCulture)
        };

        AppendCountryCodes(query, request.CountryCodes);
        AppendEmail(query);

        var uri = QueryHelpers.AddQueryString("search", query);
        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Nominatim search request failed with status code {(int)response.StatusCode}.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var searchResults = await JsonSerializer.DeserializeAsync(
            responseStream,
            NominatimJsonContext.Default.NominatimSearchResultArray,
            cancellationToken).ConfigureAwait(false) ?? [];

        var candidates = new List<GeocodeCandidate>(searchResults.Length);
        foreach (var result in searchResults)
        {
            if (!TryParseCoordinate(result.Longitude, out var x) ||
                !TryParseCoordinate(result.Latitude, out var y))
            {
                continue;
            }

            var attributes = BuildAddressAttributes(result);
            var score = Math.Clamp((result.Importance ?? 0.5d) * 100d, 0d, 100d);

            candidates.Add(new GeocodeCandidate(
                Address: result.DisplayName ?? string.Empty,
                X: x,
                Y: y,
                Score: score,
                Attributes: attributes,
                ProviderId: result.PlaceId.ToString(CultureInfo.InvariantCulture)));
        }

        return candidates;
    }

    public async Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(ReverseGeocodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["format"] = "jsonv2",
            ["addressdetails"] = "1",
            ["lon"] = request.X.ToString(CultureInfo.InvariantCulture),
            ["lat"] = request.Y.ToString(CultureInfo.InvariantCulture)
        };

        AppendEmail(query);

        var uri = QueryHelpers.AddQueryString("reverse", query);
        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Nominatim reverse request failed with status code {(int)response.StatusCode}.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var reverseResult = await JsonSerializer.DeserializeAsync(
            responseStream,
            NominatimJsonContext.Default.NominatimSearchResult,
            cancellationToken).ConfigureAwait(false);

        if (reverseResult == null ||
            !TryParseCoordinate(reverseResult.Longitude, out var x) ||
            !TryParseCoordinate(reverseResult.Latitude, out var y))
        {
            return null;
        }

        var attributes = BuildAddressAttributes(reverseResult);

        return new ReverseGeocodeMatch(
            Address: reverseResult.DisplayName ?? string.Empty,
            X: x,
            Y: y,
            Attributes: attributes,
            ProviderId: reverseResult.PlaceId.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<IReadOnlyList<GeocodeSuggestion>> SuggestAsync(SuggestGeocodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureSafeBaseUrlAsync(cancellationToken).ConfigureAwait(false);

        if (!Capabilities.SupportsSuggest)
        {
            _logger.LogDebug("Nominatim suggest requested while EnableSuggestFromSearch is disabled.");
            return [];
        }

        var query = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["format"] = "jsonv2",
            ["addressdetails"] = "0",
            ["q"] = request.Text,
            ["limit"] = request.MaxResults.ToString(CultureInfo.InvariantCulture)
        };

        AppendCountryCodes(query, request.CountryCodes);
        AppendEmail(query);

        var uri = QueryHelpers.AddQueryString("search", query);
        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Nominatim suggest request failed with status code {(int)response.StatusCode}.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var searchResults = await JsonSerializer.DeserializeAsync(
            responseStream,
            NominatimJsonContext.Default.NominatimSearchResultArray,
            cancellationToken).ConfigureAwait(false) ?? [];

        return searchResults
            .Where(static result => !string.IsNullOrWhiteSpace(result.DisplayName))
            .Select(static result => new GeocodeSuggestion(
                Text: result.DisplayName!,
                MagicKey: result.PlaceId.ToString(CultureInfo.InvariantCulture),
                IsCollection: false))
            .ToArray();
    }

    public Task<IReadOnlyList<GeocodeCandidate>> BatchGeocodeAsync(BatchGeocodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<IReadOnlyList<GeocodeCandidate>>([]);
    }

    private void AppendCountryCodes(Dictionary<string, string?> query, string? requestCountryCodes)
    {
        var countryCodes = string.IsNullOrWhiteSpace(requestCountryCodes)
            ? _options.Nominatim.CountryCodes
            : requestCountryCodes;

        if (!string.IsNullOrWhiteSpace(countryCodes))
        {
            query["countrycodes"] = countryCodes.Trim().ToLowerInvariant();
        }
    }

    private void AppendEmail(Dictionary<string, string?> query)
    {
        if (!string.IsNullOrWhiteSpace(_options.Nominatim.Email))
        {
            query["email"] = _options.Nominatim.Email;
        }
    }

    private async Task EnsureSafeBaseUrlAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _validatedBaseUrl) == 1)
        {
            return;
        }

        var validation = await OutboundHttpUrlValidator
            .ValidateAsync(_options.Nominatim.BaseUrl, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Geocoding:Nominatim:BaseUrl {validation.ErrorMessage ?? "must be a valid HTTPS URL."}");
        }

        Volatile.Write(ref _validatedBaseUrl, 1);
    }

    private static Dictionary<string, string?> BuildAddressAttributes(NominatimSearchResult result)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Provider"] = ProviderName,
            ["PlaceId"] = result.PlaceId.ToString(CultureInfo.InvariantCulture),
            ["Match_addr"] = result.DisplayName,
            ["LongLabel"] = result.DisplayName
        };

        if (result.Address is { Count: > 0 })
        {
            foreach (var pair in result.Address)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    attributes[pair.Key] = pair.Value;
                }
            }
        }

        return attributes;
    }

    private static bool TryParseCoordinate(string? rawCoordinate, out double value)
    {
        return double.TryParse(rawCoordinate, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
