// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Infrastructure.Models;
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

    /// <summary>
    /// Default spatial reference (WGS84) for a reverseGeocode <c>location</c> when the
    /// location JSON does not declare its own <c>spatialReference</c>. This matches the
    /// GeoServices reverseGeocode contract where the input location defaults to 4326.
    /// </summary>
    private const int GeocodeDefaultLocationWkid = 4326;

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
            var requestedProviderName = context.Request.Query["provider"].ToString();
            var providerName = ResolveProviderName(requestedProviderName);
            var provider = _providerRegistry.GetProvider(providerName);
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

        try
        {
            var requestedProviderName = GetValue(values, "provider");
            var providerName = NormalizeProviderName(requestedProviderName);
            var resolvedProviderName = ResolveProviderName(requestedProviderName);

            if (!string.IsNullOrWhiteSpace(requestedProviderName) && _providerRegistry.GetProvider(resolvedProviderName) == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Geocoding provider '{requestedProviderName}' not found.");
            }

            if (!TryParseSearchExtent(GetValue(values, "searchExtent"), out var searchBounds, out var searchExtentError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, searchExtentError ?? "Invalid searchExtent parameter.");
            }

            var providerRequest = new Core.Features.Geocoding.Domain.ForwardGeocodeRequest(
                Query: query,
                MaxResults: maxLocations,
                SpatialReferenceWkid: _options.DefaultSpatialReferenceWkid,
                CountryCodes: GetValue(values, "countryCodes") ?? GetValue(values, "countryCode"),
                SearchBounds: searchBounds);

            var stopwatch = Stopwatch.StartNew();
            var result = await _coordinatorService.ForwardGeocodeAsync(providerRequest, providerName, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                var errorMessage = result.ErrorMessage ?? "Geocoding request failed";
                GeocodingLog.OperationFailed(_logger, "findAddressCandidates", resolvedProviderName, errorMessage, new InvalidOperationException(errorMessage));
                return MapCoordinatorErrorToResult(context, errorMessage, "Geocoding service error");
            }

            var candidates = result.Data ?? [];
            var reprojectedCandidates = new List<GeocodeCandidateResponse>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var projected = await TryReprojectGeocodePointAsync(context, candidate.X, candidate.Y, outSrid, cancellationToken).ConfigureAwait(false);
                if (projected is null)
                {
                    return CreateUnsupportedOutSrResult(context, outSrid);
                }

                reprojectedCandidates.Add(new GeocodeCandidateResponse
                {
                    Address = candidate.Address,
                    Score = candidate.Score,
                    Location = new GeocodePoint
                    {
                        X = projected.Value.X,
                        Y = projected.Value.Y,
                        SpatialReference = new GeocodeSpatialReference
                        {
                            Wkid = outSrid,
                            LatestWkid = outSrid
                        }
                    },
                    Attributes = candidate.Attributes
                });
            }

            var response = new FindAddressCandidatesResponse
            {
                SpatialReference = new GeocodeSpatialReference
                {
                    Wkid = outSrid,
                    LatestWkid = outSrid
                },
                Candidates = [.. reprojectedCandidates]
            };

            GeocodingLog.OperationCompleted(_logger, "findAddressCandidates", result.ProviderName, response.Candidates.Length, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, GeocodingJsonContext.Default.FindAddressCandidatesResponse, contentType: JsonContentType);
        }
        catch (Exception ex)
        {
            var providerName = ResolveProviderName(GetValue(values, "provider"));
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

        if (!TryParseLocation(GetValue(values, "location"), out var x, out var y, out var locationWkid))
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "location is required and must be either 'x,y' or JSON {\"x\":...,\"y\":...}.");
        }

        // The input location is interpreted in its own spatialReference, defaulting to
        // WGS84 (4326) per the GeoServices reverseGeocode contract. outSR controls only the
        // spatial reference of the returned address location, never the input location.
        var inputSrid = locationWkid ?? GeocodeDefaultLocationWkid;

        if (!TryParseSpatialReference(GetValue(values, "outSR"), inputSrid, out var outSrid))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid outSR parameter.");
        }

        try
        {
            var requestedProviderName = GetValue(values, "provider");
            var providerName = NormalizeProviderName(requestedProviderName);
            var resolvedProviderName = ResolveProviderName(requestedProviderName);

            if (!string.IsNullOrWhiteSpace(requestedProviderName) && _providerRegistry.GetProvider(resolvedProviderName) == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Geocoding provider '{requestedProviderName}' not found.");
            }

            // The input location is expressed in its own spatial reference (inputSrid).
            // Reproject it to the provider's default reference before querying, then project
            // the match back to outSR for the response.
            var inputPoint = await TryReprojectGeocodePointAsync(context, x, y, inputSrid, _options.DefaultSpatialReferenceWkid, cancellationToken).ConfigureAwait(false);
            if (inputPoint is null)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"location spatial reference {inputSrid} is not supported for reprojection.");
            }

            var langCode = GetValue(values, "langCode");
            var providerRequest = new Core.Features.Geocoding.Domain.ReverseGeocodeRequest(inputPoint.Value.X, inputPoint.Value.Y, _options.DefaultSpatialReferenceWkid)
            {
                LanguageCode = string.IsNullOrWhiteSpace(langCode) ? null : langCode.Trim()
            };

            var stopwatch = Stopwatch.StartNew();
            var result = await _coordinatorService.ReverseGeocodeAsync(providerRequest, providerName, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                var errorMessage = result.ErrorMessage ?? "Reverse geocoding request failed";
                GeocodingLog.OperationFailed(_logger, "reverseGeocode", resolvedProviderName, errorMessage, new InvalidOperationException(errorMessage));
                return MapCoordinatorErrorToResult(context, errorMessage, "Reverse geocoding service error",
                    notFoundMessage: "No matching address was found for the supplied location");
            }

            var match = result.Data;
            if (match is null)
            {
                return StandardErrorHelpers.CreateNotFound(context, "No matching address was found for the supplied location.");
            }

            var matchPoint = await TryReprojectGeocodePointAsync(context, match.X, match.Y, outSrid, cancellationToken).ConfigureAwait(false);
            if (matchPoint is null)
            {
                return CreateUnsupportedOutSrResult(context, outSrid);
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
                    X = matchPoint.Value.X,
                    Y = matchPoint.Value.Y,
                    SpatialReference = new GeocodeSpatialReference
                    {
                        Wkid = outSrid,
                        LatestWkid = outSrid
                    }
                }
            };

            GeocodingLog.OperationCompleted(_logger, "reverseGeocode", result.ProviderName, 1, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, GeocodingJsonContext.Default.ReverseGeocodeResponse, contentType: JsonContentType);
        }
        catch (Exception ex)
        {
            var providerName = ResolveProviderName(GetValue(values, "provider"));
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
            var requestedProviderName = GetValue(values, "provider");
            var providerName = NormalizeProviderName(requestedProviderName);
            var resolvedProviderName = ResolveProviderName(requestedProviderName);
            var provider = _providerRegistry.GetProvider(resolvedProviderName);

            // When the user explicitly requested a provider, validate it exists and supports the operation.
            // When using the default provider, let the coordinator handle failover to a capable provider.
            if (!string.IsNullOrWhiteSpace(requestedProviderName) && provider == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Geocoding provider '{requestedProviderName}' not found.");
            }

            if (!string.IsNullOrWhiteSpace(requestedProviderName) && provider != null && !provider.Capabilities.SupportsSuggest)
            {
                GeocodingLog.CapabilityNotSupported(_logger, "suggest", provider.Name);
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Suggest is not supported by the configured geocode provider.");
            }

            if (!TryParseSearchExtent(GetValue(values, "searchExtent"), out var suggestBounds, out var suggestExtentError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, suggestExtentError ?? "Invalid searchExtent parameter.");
            }

            Core.Features.Geocoding.Domain.GeocodePoint? biasLocation = null;
            var rawLocation = GetValue(values, "location");
            if (!string.IsNullOrWhiteSpace(rawLocation))
            {
                if (!TryParseLocation(rawLocation, out var biasX, out var biasY))
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        "location must be either 'x,y' or JSON {\"x\":...,\"y\":...}.");
                }

                biasLocation = new Core.Features.Geocoding.Domain.GeocodePoint(biasX, biasY, _options.DefaultSpatialReferenceWkid);
            }

            var providerRequest = new Core.Features.Geocoding.Domain.SuggestGeocodeRequest(
                Text: text.Trim(),
                MaxResults: maxSuggestions,
                CountryCodes: GetValue(values, "countryCodes") ?? GetValue(values, "countryCode"))
            {
                SearchBounds = suggestBounds,
                BiasLocation = biasLocation
            };

            var stopwatch = Stopwatch.StartNew();

            var result = await _coordinatorService.SuggestAsync(providerRequest, providerName, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                var errorMessage = result.ErrorMessage ?? "Suggest request failed";
                GeocodingLog.OperationFailed(_logger, "suggest", resolvedProviderName, errorMessage, new InvalidOperationException(errorMessage));
                return MapCoordinatorErrorToResult(context, errorMessage, "Suggest service error");
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

            GeocodingLog.OperationCompleted(_logger, "suggest", result.ProviderName, response.Suggestions.Length, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, GeocodingJsonContext.Default.SuggestResponse, contentType: JsonContentType);
        }
        catch (Exception ex)
        {
            var providerName = ResolveProviderName(GetValue(values, "provider"));
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

        var requestedProviderName = GetValue(values, "provider");
        var providerName = ResolveProviderName(requestedProviderName);
        var resolvedProviderName = providerName;
        var provider = _providerRegistry.GetProvider(providerName);

        if (!string.IsNullOrWhiteSpace(requestedProviderName) && provider == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"Geocoding provider '{requestedProviderName}' not found.");
        }

        // Only block when the user explicitly requested a specific provider that lacks batch support.
        // When using the default provider, let the coordinator handle failover to a capable provider.
        if (!string.IsNullOrWhiteSpace(requestedProviderName) && provider != null && !provider.Capabilities.SupportsBatch)
        {
            GeocodingLog.CapabilityNotSupported(_logger, "geocodeAddresses", provider.Name);
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "Batch geocoding is not supported by the configured geocode provider.");
        }

        // Esri clients (ArcGIS API for Python batch_geocode, ArcGIS Pro) send the batch payload
        // under the "addresses" parameter, e.g. addresses={"records":[{"attributes":{...}}]}.
        // Accept that as the primary name and fall back to "records" for direct callers.
        var recordsJson = GetValue(values, "addresses") ?? GetValue(values, "records");
        if (!TryParseRecords(recordsJson, out var queries, out var parseError))
        {
            GeocodingLog.BatchRecordsParseFailed(_logger, parseError ?? "Unknown parse error");
            return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid records parameter.");
        }

        if (queries is null || queries.Count == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "records parameter is required and must contain at least one address.");
        }

        // Use the explicit provider's limit when one was requested; otherwise use a
        // default cap since the coordinator may route to a different provider via failover.
        var maxBatchSize = !string.IsNullOrWhiteSpace(requestedProviderName)
            ? (provider?.Capabilities.MaxBatchSize ?? 100)
            : 100;
        if (queries.Count > maxBatchSize)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                $"Batch size {queries.Count} exceeds the maximum allowed batch size of {maxBatchSize}.");
        }

        if (!TryParseSpatialReference(GetValue(values, "outSR"), _options.DefaultSpatialReferenceWkid, out var outSrid))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid outSR parameter.");
        }

        try
        {
            var batchRequest = new Core.Features.Geocoding.Domain.BatchGeocodeRequest(
                Queries: queries,
                SpatialReferenceWkid: _options.DefaultSpatialReferenceWkid,
                CountryCodes: GetValue(values, "countryCodes") ?? GetValue(values, "countryCode"));

            var stopwatch = Stopwatch.StartNew();
            var result = await _coordinatorService.BatchGeocodeAsync(batchRequest, NormalizeProviderName(requestedProviderName), cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                var errorMessage = result.ErrorMessage ?? "Batch geocoding request failed";
                GeocodingLog.OperationFailed(_logger, "geocodeAddresses", resolvedProviderName, errorMessage, new InvalidOperationException(errorMessage));
                return MapCoordinatorErrorToResult(context, errorMessage, "Batch geocoding service error");
            }

            var candidates = result.Data ?? [];
            var reprojectedLocations = new List<GeocodeAddressLocation>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var projected = await TryReprojectGeocodePointAsync(context, candidate.X, candidate.Y, outSrid, cancellationToken).ConfigureAwait(false);
                if (projected is null)
                {
                    return CreateUnsupportedOutSrResult(context, outSrid);
                }

                reprojectedLocations.Add(new GeocodeAddressLocation
                {
                    Address = candidate.Address,
                    Score = candidate.Score,
                    Location = new GeocodePoint
                    {
                        X = projected.Value.X,
                        Y = projected.Value.Y,
                        SpatialReference = new GeocodeSpatialReference
                        {
                            Wkid = outSrid,
                            LatestWkid = outSrid
                        }
                    },
                    Attributes = candidate.Attributes
                });
            }

            var response = new GeocodeAddressesResponse
            {
                SpatialReference = new GeocodeSpatialReference
                {
                    Wkid = outSrid,
                    LatestWkid = outSrid
                },
                Locations = [.. reprojectedLocations]
            };

            GeocodingLog.OperationCompleted(_logger, "geocodeAddresses", result.ProviderName, response.Locations.Length, stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(response, GeocodingJsonContext.Default.GeocodeAddressesResponse, contentType: JsonContentType);
        }
        catch (Exception ex)
        {
            GeocodingLog.OperationFailed(_logger, "geocodeAddresses", resolvedProviderName, ex.Message, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Batch geocoding request failed.");
        }
    }

    private static bool TryParseRecords(string? recordsJson, out List<string>? queries, out string? error)
    {
        queries = null;
        error = null;

        if (string.IsNullOrWhiteSpace(recordsJson))
        {
            error = "records parameter is required and must contain at least one address.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(recordsJson);
            var root = document.RootElement;

            JsonElement recordsArray;
            if (root.ValueKind == JsonValueKind.Array)
            {
                recordsArray = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("records", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                recordsArray = nested;
            }
            else
            {
                error = "records must be a JSON array or an object with a 'records' array property.";
                return false;
            }

            queries = new List<string>(recordsArray.GetArrayLength());
            List<int>? invalidIndices = null;
            var index = 0;
            foreach (var record in recordsArray.EnumerateArray())
            {
                var address = ExtractAddressFromRecord(record);
                if (!string.IsNullOrWhiteSpace(address))
                {
                    queries.Add(address);
                }
                else
                {
                    invalidIndices ??= [];
                    invalidIndices.Add(index);
                }

                index++;
            }

            if (invalidIndices is { Count: > 0 })
            {
                queries = null;
                error = invalidIndices.Count == 1
                    ? $"Record at index {invalidIndices[0]} does not contain a valid address."
                    : $"Records at indices {string.Join(", ", invalidIndices)} do not contain valid addresses.";
                return false;
            }

            if (queries.Count == 0)
            {
                error = "No valid address records found in the request.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "records parameter contains invalid JSON.";
            return false;
        }
    }

    private static string? ExtractAddressFromRecord(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.String)
        {
            return record.GetString();
        }

        if (record.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Try attributes.SingleLine first
        if (record.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
        {
            if (attributes.TryGetProperty("SingleLine", out var singleLine) && singleLine.ValueKind == JsonValueKind.String)
            {
                var value = singleLine.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            // Concatenate structured fields
            var parts = new[]
            {
                TryGetStringProperty(attributes, "Address"),
                TryGetStringProperty(attributes, "City"),
                TryGetStringProperty(attributes, "Region"),
                TryGetStringProperty(attributes, "Postal"),
                TryGetStringProperty(attributes, "CountryCode")
            }
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part!.Trim());

            var joined = string.Join(", ", parts);
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return joined;
            }
        }

        // Try top-level address/singleLine as fallback
        if (record.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String)
        {
            return addr.GetString();
        }

        if (record.TryGetProperty("singleLine", out var sl) && sl.ValueKind == JsonValueKind.String)
        {
            return sl.GetString();
        }

        return null;
    }

    private static string? TryGetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static IResult MapCoordinatorErrorToResult(
        HttpContext context,
        string errorMessage,
        string fallbackMessage,
        string? notFoundMessage = null)
    {
        if (errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateUnauthorized(context, "Authentication failed for geocoding service");
        }

        if (errorMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Rate limit exceeded");
        }

        if (errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("bad request", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
        }

        if (notFoundMessage is not null &&
            (errorMessage.Contains("no results", StringComparison.OrdinalIgnoreCase) ||
             errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return StandardErrorHelpers.CreateNotFound(context, notFoundMessage);
        }

        if (errorMessage.Contains("no providers available", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "No configured geocoding provider supports this operation.");
        }

        return StandardErrorHelpers.CreateInternalServerError(context, fallbackMessage);
    }

    private static string? NormalizeProviderName(string? providerName)
    {
        return string.IsNullOrWhiteSpace(providerName)
            ? null
            : providerName.Trim();
    }

    private string ResolveProviderName(string? providerName)
    {
        return NormalizeProviderName(providerName) ?? _options.DefaultProvider;
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

    /// <summary>
    /// Reprojects a point from the provider's default reference to the requested output SRID,
    /// returning <see langword="null"/> when the transform is not supported.
    /// </summary>
    private ValueTask<(double X, double Y)?> TryReprojectGeocodePointAsync(
        HttpContext context,
        double x,
        double y,
        int outSrid,
        CancellationToken cancellationToken)
        => TryReprojectGeocodePointAsync(context, x, y, _options.DefaultSpatialReferenceWkid, outSrid, cancellationToken);

    /// <summary>
    /// Reprojects a point between two SRIDs using the shared coordinate transform service.
    /// Returns the input unchanged when both SRIDs match, or <see langword="null"/> when the
    /// transform cannot be performed.
    /// </summary>
    private static ValueTask<(double X, double Y)?> TryReprojectGeocodePointAsync(
        HttpContext context,
        double x,
        double y,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        if (fromSrid == toSrid)
        {
            return ValueTask.FromResult<(double X, double Y)?>((x, y));
        }

        var transformService = context.RequestServices.GetRequiredService<ICoordinateTransformService>();
        return transformService.TransformPointAsync(x, y, fromSrid, toSrid, cancellationToken);
    }

    private static IResult CreateUnsupportedOutSrResult(HttpContext context, int outSrid)
        => StandardErrorHelpers.CreateBadRequest(
            context,
            $"outSR={outSrid} is not supported for reprojection.");

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

    private bool TryParseSearchExtent(
        string? rawExtent,
        out Core.Features.Geocoding.Domain.GeocodeBounds? bounds,
        out string? error)
    {
        bounds = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawExtent))
        {
            return true;
        }

        var trimmed = rawExtent.Trim();

        double xmin, ymin, xmax, ymax;
        var extentWkid = _options.DefaultSpatialReferenceWkid;

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var root = document.RootElement;

                if (!TryReadCoordinate(root, "xmin", out xmin) ||
                    !TryReadCoordinate(root, "ymin", out ymin) ||
                    !TryReadCoordinate(root, "xmax", out xmax) ||
                    !TryReadCoordinate(root, "ymax", out ymax))
                {
                    error = "searchExtent must contain numeric xmin, ymin, xmax, and ymax values.";
                    return false;
                }

                if (root.TryGetProperty("spatialReference", out var sr) && sr.ValueKind == JsonValueKind.Object)
                {
                    if (sr.TryGetProperty("wkid", out var wkidElement) && wkidElement.ValueKind == JsonValueKind.Number &&
                        wkidElement.TryGetInt32(out var parsedWkid))
                    {
                        extentWkid = parsedWkid;
                    }
                    else if (sr.TryGetProperty("latestWkid", out var latestElement) && latestElement.ValueKind == JsonValueKind.Number &&
                             latestElement.TryGetInt32(out var parsedLatest))
                    {
                        extentWkid = parsedLatest;
                    }
                }
            }
            catch (JsonException)
            {
                error = "searchExtent contains invalid JSON.";
                return false;
            }
        }
        else
        {
            var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out xmin) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ymin) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out xmax) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out ymax))
            {
                error = "searchExtent must be 'xmin,ymin,xmax,ymax' or a JSON envelope.";
                return false;
            }
        }

        if (extentWkid != _options.DefaultSpatialReferenceWkid)
        {
            error = $"Only searchExtent in spatial reference {_options.DefaultSpatialReferenceWkid} is currently supported.";
            return false;
        }

        if (xmin > xmax || ymin > ymax)
        {
            error = "searchExtent is invalid: xmin/ymin must not exceed xmax/ymax.";
            return false;
        }

        bounds = new Core.Features.Geocoding.Domain.GeocodeBounds(xmin, ymin, xmax, ymax, extentWkid);
        return true;
    }

    private static bool TryParseLocation(string? rawLocation, out double x, out double y)
        => TryParseLocation(rawLocation, out x, out y, out _);

    /// <summary>
    /// Parses a reverse-geocode <c>location</c> parameter into x/y coordinates and the
    /// spatial reference declared by the location JSON itself (Esri location geometries
    /// carry their own <c>spatialReference</c>). The input SR defaults to WGS84 (4326)
    /// when the location is a bare "x,y" pair or carries no spatialReference, matching
    /// the GeoServices reverseGeocode default. The output SR (<c>outSR</c>) is handled
    /// separately by the caller and never used to interpret the input location.
    /// </summary>
    private static bool TryParseLocation(string? rawLocation, out double x, out double y, out int? inputWkid)
    {
        x = default;
        y = default;
        inputWkid = null;

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

                if (root.TryGetProperty("spatialReference", out var sr) && sr.ValueKind == JsonValueKind.Object)
                {
                    if (sr.TryGetProperty("wkid", out var wkidElement) && wkidElement.ValueKind == JsonValueKind.Number &&
                        wkidElement.TryGetInt32(out var parsedWkid) && parsedWkid > 0)
                    {
                        inputWkid = parsedWkid;
                    }
                    else if (sr.TryGetProperty("latestWkid", out var latestElement) && latestElement.ValueKind == JsonValueKind.Number &&
                             latestElement.TryGetInt32(out var parsedLatest) && parsedLatest > 0)
                    {
                        inputWkid = parsedLatest;
                    }
                }

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
