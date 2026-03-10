// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Geocoding;

internal sealed class GeocodingHandler(
    IGeocodeCoordinatorService coordinatorService,
    IGeocodeProviderRegistry providerRegistry,
    IOptions<GeocodingOptions> options,
    ILogger<GeocodingLogCategory> logger)
{
    private const string JsonFormat = "json";
    private const string PrettyJsonFormat = "pjson";
    private const string JsonContentType = "application/json";

    private readonly IGeocodeCoordinatorService _coordinatorService = coordinatorService ?? throw new ArgumentNullException(nameof(coordinatorService));
    private readonly IGeocodeProviderRegistry _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    private readonly GeocodingOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<GeocodingLogCategory> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<IResult> HandleMetadataAsync(HttpContext context, string? locatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var locatorValidation = ValidateLocatorName(context, locatorName);
        if (locatorValidation != null)
        {
            return Task.FromResult(locatorValidation);
        }

        if (!IsSupportedFormat(context.Request.Query["f"].ToString()))
        {
            return Task.FromResult(StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson."));
        }

        try
        {
            var providerName = context.Request.Query["provider"].ToString();
            var provider = _providerRegistry.GetProvider(providerName ?? _options.DefaultProvider);
            if (provider == null)
            {
                return Task.FromResult(StandardErrorHelpers.CreateBadRequest(context, "Geocoding provider not found or not configured."));
            }

            var capabilities = provider.Capabilities;

            var response = new GeocodeServerInfoResponse
            {
                ServiceDescription = "Honua GeocodeServer",
                Capabilities = BuildCapabilitiesString(capabilities),
                SpatialReference = new GeocodeSpatialReference
                {
                    Wkid = _options.DefaultSpatialReferenceWkid,
                    LatestWkid = _options.DefaultSpatialReferenceWkid
                },
                LocatorProperties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LocatorName"] = _options.LocatorName,
                    ["Provider"] = provider.Name,
                    ["SupportsSuggest"] = capabilities.SupportsSuggest ? "true" : "false",
                    ["SupportsBatch"] = capabilities.SupportsBatch ? "true" : "false"
                }
            };

            return Task.FromResult<IResult>(Results.Json(response, GeocodingJsonContext.Default.GeocodeServerInfoResponse, contentType: JsonContentType));
        }
        catch (Exception ex)
        {
            GeocodingLog.OperationFailed(_logger, "metadata", "unknown", ex.Message, ex);
            return Task.FromResult(StandardErrorHelpers.CreateInternalServerError(context, "Unable to resolve geocoding provider configuration."));
        }
    }

    public async Task<IResult> HandleFindAddressCandidatesAsync(HttpContext context, string? locatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var locatorValidation = ValidateLocatorName(context, locatorName);
        if (locatorValidation != null)
        {
            return locatorValidation;
        }

        var (values, readError) = await TryReadGeocodeRequestValuesAsync(context, cancellationToken).ConfigureAwait(false);
        if (values is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        if (!IsSupportedFormat(GetValue(values, "f")))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var query = BuildForwardQuery(values);
        if (string.IsNullOrWhiteSpace(query))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "singleLine is required, or provide structured fields: address, city, region, postal, countryCode.");
        }

        if (!TryParsePositiveInt(
            GetValue(values, "maxLocations"),
            _options.Nominatim.DefaultMaxResults,
            out var maxLocations,
            out var maxLocationsError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, maxLocationsError ?? "Invalid maxLocations parameter.");
        }

        if (!TryParseSpatialReference(GetValue(values, "outSR"), _options.DefaultSpatialReferenceWkid, out var outSrid))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid outSR parameter.");
        }

        if (outSrid != _options.DefaultSpatialReferenceWkid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Only outSR={_options.DefaultSpatialReferenceWkid} is currently supported.");
        }

        try
        {
            var providerName = GetValue(values, "provider");
            var providerRequest = new Core.Features.Geocoding.Domain.ForwardGeocodeRequest(
                Query: query,
                MaxResults: maxLocations,
                SpatialReferenceWkid: outSrid,
                CountryCodes: GetValue(values, "countryCodes") ?? GetValue(values, "countryCode"));

            var stopwatch = Stopwatch.StartNew();
            var result = await _coordinatorService.ForwardGeocodeAsync(providerRequest, providerName, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                var errorMessage = result.ErrorMessage ?? "Geocoding request failed";

                GeocodingLog.OperationFailed(_logger, "findAddressCandidates", providerName ?? _options.DefaultProvider, errorMessage, new InvalidOperationException(errorMessage));

                // Check error message content to determine response type
                if (errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateUnauthorized(context, "Authentication failed for geocoding service");
                }
                else if (errorMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                         errorMessage.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateServiceUnavailable(context, "Rate limit exceeded");
                }
                else if (errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                         errorMessage.Contains("bad request", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }
                else
                {
                    return StandardErrorHelpers.CreateInternalServerError(context, "Geocoding service error");
                }
            }

            var candidates = result.Data ?? [];
            var response = new FindAddressCandidatesResponse
            {
                SpatialReference = new GeocodeSpatialReference
                {
                    Wkid = outSrid,
                    LatestWkid = outSrid
                },
                Candidates = [.. candidates.Select(candidate => new GeocodeCandidateResponse
                {
                    Address = candidate.Address,
                    Score = candidate.Score,
                    Location = new GeocodePoint
                    {
                        X = candidate.X,
                        Y = candidate.Y,
                        SpatialReference = new GeocodeSpatialReference
                        {
                            Wkid = outSrid,
                            LatestWkid = outSrid
                        }
                    },
                    Attributes = candidate.Attributes
                })]
            };

            var provider = _providerRegistry.GetProvider(providerName ?? _options.DefaultProvider);
            var usedProviderName = provider?.Name ?? providerName ?? _options.DefaultProvider;

            GeocodingLog.OperationCompleted(_logger, "findAddressCandidates", usedProviderName, response.Candidates.Length, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, GeocodingJsonContext.Default.FindAddressCandidatesResponse, contentType: JsonContentType);
        }
        catch (Exception ex)
        {
            var providerName = GetValue(values, "provider") ?? _options.DefaultProvider;
            GeocodingLog.OperationFailed(_logger, "findAddressCandidates", providerName, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Geocoding request failed.");
        }
    }

    public async Task<IResult> HandleReverseGeocodeAsync(HttpContext context, string? locatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var locatorValidation = ValidateLocatorName(context, locatorName);
        if (locatorValidation != null)
        {
            return locatorValidation;
        }

        var (values, readError) = await TryReadGeocodeRequestValuesAsync(context, cancellationToken).ConfigureAwait(false);
        if (values is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        if (!IsSupportedFormat(GetValue(values, "f")))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        if (!TryParseLocation(GetValue(values, "location"), out var x, out var y))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "location is required and must be either 'x,y' or JSON {\"x\":...,\"y\":...}.");
        }

        if (!TryParseSpatialReference(GetValue(values, "outSR"), _options.DefaultSpatialReferenceWkid, out var outSrid))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid outSR parameter.");
        }

        if (outSrid != _options.DefaultSpatialReferenceWkid)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Only outSR={_options.DefaultSpatialReferenceWkid} is currently supported.");
        }

        try
        {
            var providerName = GetValue(values, "provider");
            var providerRequest = new Core.Features.Geocoding.Domain.ReverseGeocodeRequest(x, y, outSrid);

            var stopwatch = Stopwatch.StartNew();
            var result = await _coordinatorService.ReverseGeocodeAsync(providerRequest, providerName, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                var errorMessage = result.ErrorMessage ?? "Reverse geocoding request failed";

                GeocodingLog.OperationFailed(_logger, "reverseGeocode", providerName ?? _options.DefaultProvider, errorMessage, new InvalidOperationException(errorMessage));

                // Check error message content to determine response type
                if (errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateUnauthorized(context, "Authentication failed for geocoding service");
                }
                else if (errorMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                         errorMessage.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateServiceUnavailable(context, "Rate limit exceeded");
                }
                else if (errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                         errorMessage.Contains("bad request", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }
                else if (errorMessage.Contains("no results", StringComparison.OrdinalIgnoreCase) ||
                         errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateNotFound(context, "No matching address was found for the supplied location");
                }
                else
                {
                    return StandardErrorHelpers.CreateInternalServerError(context, "Reverse geocoding service error");
                }
            }

            var match = result.Data;
            if (match is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "No matching address was found for the supplied location.");
            }

            var address = new Dictionary<string, string?>(match.Attributes, StringComparer.Ordinal)
            {
                ["Match_addr"] = match.Address,
                ["LongLabel"] = match.Address
            };

            var response = new ReverseGeocodeResponse
            {
                Address = address,
                Location = new GeocodePoint
                {
                    X = match.X,
                    Y = match.Y,
                    SpatialReference = new GeocodeSpatialReference
                    {
                        Wkid = outSrid,
                        LatestWkid = outSrid
                    }
                }
            };

            var provider = _providerRegistry.GetProvider(providerName ?? _options.DefaultProvider);
            var usedProviderName = provider?.Name ?? providerName ?? _options.DefaultProvider;

            GeocodingLog.OperationCompleted(_logger, "reverseGeocode", usedProviderName, 1, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, GeocodingJsonContext.Default.ReverseGeocodeResponse, contentType: JsonContentType);
        }
        catch (Exception ex)
        {
            var providerName = GetValue(values, "provider") ?? _options.DefaultProvider;
            GeocodingLog.OperationFailed(_logger, "reverseGeocode", providerName, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Reverse geocoding request failed.");
        }
    }

    public async Task<IResult> HandleSuggestAsync(HttpContext context, string? locatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var locatorValidation = ValidateLocatorName(context, locatorName);
        if (locatorValidation != null)
        {
            return locatorValidation;
        }

        var (values, readError) = await TryReadGeocodeRequestValuesAsync(context, cancellationToken).ConfigureAwait(false);
        if (values is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        if (!IsSupportedFormat(GetValue(values, "f")))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var text = GetValue(values, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "text is required.");
        }

        if (!TryParsePositiveInt(
            GetValue(values, "maxSuggestions"),
            _options.Nominatim.DefaultMaxSuggestions,
            out var maxSuggestions,
            out var maxSuggestionsError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, maxSuggestionsError ?? "Invalid maxSuggestions parameter.");
        }

        try
        {
            var providerName = GetValue(values, "provider");
            var provider = _providerRegistry.GetProvider(providerName ?? _options.DefaultProvider);
            if (provider != null && !provider.Capabilities.SupportsSuggest)
            {
                GeocodingLog.CapabilityNotSupported(_logger, "suggest", provider.Name);
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Suggest is not supported by the configured geocode provider.");
            }

            var providerRequest = new Core.Features.Geocoding.Domain.SuggestGeocodeRequest(
                Text: text.Trim(),
                MaxResults: maxSuggestions,
                CountryCodes: GetValue(values, "countryCodes") ?? GetValue(values, "countryCode"));

            var stopwatch = Stopwatch.StartNew();

            var result = await _coordinatorService.SuggestAsync(providerRequest, providerName, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                var errorMessage = result.ErrorMessage ?? "Suggest request failed";

                GeocodingLog.OperationFailed(_logger, "suggest", providerName ?? _options.DefaultProvider, errorMessage, new InvalidOperationException(errorMessage));

                // Check error message content to determine response type
                if (errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateUnauthorized(context, "Authentication failed for geocoding service");
                }
                else if (errorMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                         errorMessage.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateServiceUnavailable(context, "Rate limit exceeded");
                }
                else if (errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                         errorMessage.Contains("bad request", StringComparison.OrdinalIgnoreCase))
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }
                else
                {
                    return StandardErrorHelpers.CreateInternalServerError(context, "Suggest service error");
                }
            }

            var suggestions = result.Data ?? [];
            var response = new SuggestResponse
            {
                Suggestions = [.. suggestions.Select(suggestion => new GeocodeSuggestionResponse
                {
                    Text = suggestion.Text,
                    MagicKey = suggestion.MagicKey,
                    IsCollection = suggestion.IsCollection
                })]
            };

            var usedProviderName = provider?.Name ?? providerName ?? _options.DefaultProvider;

            GeocodingLog.OperationCompleted(_logger, "suggest", usedProviderName, response.Suggestions.Length, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, GeocodingJsonContext.Default.SuggestResponse, contentType: JsonContentType);
        }
        catch (Exception ex)
        {
            var providerName = GetValue(values, "provider") ?? _options.DefaultProvider;
            GeocodingLog.OperationFailed(_logger, "suggest", providerName, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Suggest request failed.");
        }
    }

    public async Task<IResult> HandleBatchGeocodeAsync(HttpContext context, string? locatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var locatorValidation = ValidateLocatorName(context, locatorName);
        if (locatorValidation != null)
        {
            return locatorValidation;
        }

        var (values, readError) = await TryReadGeocodeRequestValuesAsync(context, cancellationToken).ConfigureAwait(false);
        if (values is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, readError ?? "Invalid request body.");
        }

        if (!IsSupportedFormat(GetValue(values, "f")))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var providerName = GetValue(values, "provider");
        var provider = _providerRegistry.GetProvider(providerName ?? _options.DefaultProvider);
        if (provider != null && !provider.Capabilities.SupportsBatch)
        {
            GeocodingLog.CapabilityNotSupported(_logger, "geocodeAddresses", provider.Name);
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Batch geocoding is not supported by the configured geocode provider.");
        }

        return StandardErrorHelpers.CreateBadRequest(
            context,
            "Batch geocoding request parsing is not yet available in this release.");
    }

    private IResult? ValidateLocatorName(HttpContext context, string? locatorName)
    {
        if (!_options.Enabled)
        {
            return StandardErrorHelpers.CreateNotFound(context, "Geocoding service is disabled.");
        }

        if (string.IsNullOrWhiteSpace(locatorName))
        {
            return null;
        }

        if (!string.Equals(locatorName, _options.LocatorName, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Geocode service '{locatorName}' not found.");
        }

        return null;
    }

    private static string? BuildForwardQuery(IReadOnlyDictionary<string, StringValues> values)
    {
        var singleLine = GetValue(values, "singleLine") ?? GetValue(values, "SingleLine");
        if (!string.IsNullOrWhiteSpace(singleLine))
        {
            return singleLine.Trim();
        }

        var structuredParts = new[]
        {
            GetValue(values, "address"),
            GetValue(values, "city"),
            GetValue(values, "region"),
            GetValue(values, "postal"),
            GetValue(values, "countryCode")
        }
        .Where(static part => !string.IsNullOrWhiteSpace(part))
        .Select(static part => part!.Trim());

        var joined = string.Join(", ", structuredParts);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private static string BuildCapabilitiesString(Core.Features.Geocoding.Domain.GeocodeProviderCapabilities capabilities)
    {
        var availableCapabilities = new List<string>(capacity: 4)
        {
            "Geocode",
            "ReverseGeocode"
        };

        if (capabilities.SupportsSuggest)
        {
            availableCapabilities.Add("Suggest");
        }

        if (capabilities.SupportsBatch)
        {
            availableCapabilities.Add("BatchGeocode");
        }

        return string.Join(',', availableCapabilities);
    }

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, JsonFormat, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, PrettyJsonFormat, StringComparison.OrdinalIgnoreCase);

    private static string? GetValue(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;

    private static async Task<(Dictionary<string, StringValues>? Values, string? Error)> TryReadGeocodeRequestValuesAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);

        if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsPut(context.Request.Method))
        {
            return (values, null);
        }

        if (!context.Request.HasFormContentType && (context.Request.ContentLength is null or 0))
        {
            return (values, null);
        }

        var (bodyValues, error) = await FeatureServerEndpoints.TryReadRequestValuesAsync(
            context.Request,
            cancellationToken).ConfigureAwait(false);
        if (bodyValues is null)
        {
            return (null, error ?? "Invalid request body.");
        }

        foreach (var pair in bodyValues)
        {
            values[pair.Key] = pair.Value;
        }

        return (values, null);
    }

    private static bool TryParsePositiveInt(string? rawValue, int defaultValue, out int value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            value = defaultValue;
            return true;
        }

        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = "Value must be a positive integer.";
            return false;
        }

        return true;
    }

    private static bool TryParseSpatialReference(string? rawOutSpatialReference, int defaultWkid, out int wkid)
    {
        if (string.IsNullOrWhiteSpace(rawOutSpatialReference))
        {
            wkid = defaultWkid;
            return true;
        }

        if (int.TryParse(rawOutSpatialReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out wkid) && wkid > 0)
        {
            return true;
        }

        wkid = 0;
        return false;
    }

    private static bool TryParseLocation(string? rawLocation, out double x, out double y)
    {
        x = default;
        y = default;

        if (string.IsNullOrWhiteSpace(rawLocation))
        {
            return false;
        }

        var trimmed = rawLocation.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;

                if (TryReadCoordinate(root, "x", out x) && TryReadCoordinate(root, "y", out y))
                {
                    return true;
                }

                if (TryReadCoordinate(root, "lon", out x) && TryReadCoordinate(root, "lat", out y))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }

            return false;
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    private static bool TryReadCoordinate(JsonElement root, string name, out double value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out value);
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }
}
