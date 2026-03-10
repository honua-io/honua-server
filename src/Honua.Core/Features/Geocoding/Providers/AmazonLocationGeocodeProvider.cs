// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Amazon;
using Amazon.LocationService;
using Amazon.LocationService.Model;
using Amazon.Runtime;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;

namespace Honua.Core.Features.Geocoding.Providers;

/// <summary>
/// Amazon Location Service geocoding provider
/// </summary>
public sealed class AmazonLocationGeocodeProvider : BaseGeocodeProvider, IDisposable
{
    /// <summary>
    /// Amazon Location Service provider name constant
    /// </summary>
    public const string ProviderName = "amazon-location";

    private readonly AmazonLocationProviderConfiguration _configuration;
    private readonly IAmazonLocationService _locationClient;
    private readonly bool _disposeClient;

    /// <summary>
    /// Initialize a new Amazon Location Service geocode provider
    /// </summary>
    /// <param name="configuration">Provider configuration</param>
    /// <param name="locationClient">Optional pre-configured Amazon Location Service client</param>
    public AmazonLocationGeocodeProvider(
        AmazonLocationProviderConfiguration configuration,
        IAmazonLocationService? locationClient = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        if (string.IsNullOrWhiteSpace(configuration.PlaceIndexName))
        {
            throw new GeocodeProviderException("Amazon Location place index name is required")
            {
                ProviderName = ProviderName,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }

        if (locationClient != null)
        {
            _locationClient = locationClient;
            _disposeClient = false;
        }
        else
        {
            _locationClient = CreateLocationClient(configuration);
            _disposeClient = true;
        }
    }

    /// <inheritdoc />
    public override string Name => ProviderName;

    /// <inheritdoc />
    public override GeocodeProviderCapabilities Capabilities => new(
        SupportsForwardGeocode: true,
        SupportsReverseGeocode: true,
        SupportsSuggest: true,
        SupportsBatch: false, // AWS Location Service doesn't have native batch support
        SupportsStructuredInput: false,
        SupportsBiasing: true)
    {
        SupportedSpatialReferences = [4326], // AWS Location returns WGS84 coordinates
        MaxResultsPerRequest = 50,
        MaxBatchSize = 0,
        RequiresAuthentication = true,
        SupportedLanguages = ["en", "ar", "cs", "da", "de", "en", "es", "fi", "fr", "he", "hu", "it", "ja", "ko", "nb", "nl", "pl", "pt", "ru", "sv", "th", "tr", "zh"],
        DefaultTimeoutSeconds = _configuration.TimeoutSeconds,
        RateLimitPerMinute = 100 // AWS Location Service default limit
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

        try
        {
            var searchRequest = new SearchPlaceIndexForTextRequest
            {
                IndexName = _configuration.PlaceIndexName,
                Text = request.Query,
                MaxResults = ValidateMaxResults(request.MaxResults)
            };

            // Add country filter if provided
            if (!string.IsNullOrWhiteSpace(request.CountryCodes))
            {
                searchRequest.FilterCountries = request.CountryCodes
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim().ToUpperInvariant())
                    .ToList();
            }

            // Add bias position if search bounds are provided
            if (request.SearchBounds != null)
            {
                var centerX = (request.SearchBounds.XMin + request.SearchBounds.XMax) / 2;
                var centerY = (request.SearchBounds.YMin + request.SearchBounds.YMax) / 2;
                searchRequest.BiasPosition = [centerX, centerY];
            }

            var response = await _locationClient.SearchPlaceIndexForTextAsync(searchRequest, cancellationToken).ConfigureAwait(false);

            return ConvertSearchResults(response.Results, request);
        }
        catch (Amazon.LocationService.Model.ResourceNotFoundException ex)
        {
            throw new GeocodeProviderException($"Amazon Location place index '{_configuration.PlaceIndexName}' not found: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }
        catch (Amazon.LocationService.Model.ThrottlingException ex)
        {
            throw new GeocodeRateLimitException($"Amazon Location rate limit exceeded: {ex.Message}", ex)
            {
                ProviderName = Name,
                RetryAfter = TimeSpan.FromSeconds(60) // Default retry after 60 seconds
            };
        }
        catch (Amazon.LocationService.Model.AccessDeniedException ex)
        {
            throw new GeocodeAuthenticationException($"Amazon Location access denied: {ex.Message}", ex)
            {
                ProviderName = Name
            };
        }
        catch (AmazonServiceException ex)
        {
            throw new GeocodeProviderException($"Amazon Location service error: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
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

        try
        {
            var reverseRequest = new SearchPlaceIndexForPositionRequest
            {
                IndexName = _configuration.PlaceIndexName,
                Position = [request.X, request.Y],
                MaxResults = 1
            };

            // Add language preference if provided
            if (!string.IsNullOrWhiteSpace(request.LanguageCode))
            {
                reverseRequest.Language = request.LanguageCode;
            }

            var response = await _locationClient.SearchPlaceIndexForPositionAsync(reverseRequest, cancellationToken).ConfigureAwait(false);

            return ConvertReverseResult(response.Results.FirstOrDefault(), request);
        }
        catch (Amazon.LocationService.Model.ResourceNotFoundException ex)
        {
            throw new GeocodeProviderException($"Amazon Location place index '{_configuration.PlaceIndexName}' not found: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }
        catch (Amazon.LocationService.Model.ThrottlingException ex)
        {
            throw new GeocodeRateLimitException($"Amazon Location rate limit exceeded: {ex.Message}", ex)
            {
                ProviderName = Name,
                RetryAfter = TimeSpan.FromSeconds(60) // Default retry after 60 seconds
            };
        }
        catch (Amazon.LocationService.Model.AccessDeniedException ex)
        {
            throw new GeocodeAuthenticationException($"Amazon Location access denied: {ex.Message}", ex)
            {
                ProviderName = Name
            };
        }
        catch (AmazonServiceException ex)
        {
            throw new GeocodeProviderException($"Amazon Location service error: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
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

        try
        {
            var suggestRequest = new SearchPlaceIndexForSuggestionsRequest
            {
                IndexName = _configuration.PlaceIndexName,
                Text = request.Text,
                MaxResults = ValidateMaxResults(request.MaxResults)
            };

            // Add country filter if provided
            if (!string.IsNullOrWhiteSpace(request.CountryCodes))
            {
                suggestRequest.FilterCountries = request.CountryCodes
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim().ToUpperInvariant())
                    .ToList();
            }

            // Add bias position if provided
            if (request.BiasLocation != null)
            {
                suggestRequest.BiasPosition = [request.BiasLocation.X, request.BiasLocation.Y];
            }

            var response = await _locationClient.SearchPlaceIndexForSuggestionsAsync(suggestRequest, cancellationToken).ConfigureAwait(false);

            return ConvertSuggestionResults(response.Results);
        }
        catch (Amazon.LocationService.Model.ResourceNotFoundException ex)
        {
            throw new GeocodeProviderException($"Amazon Location place index '{_configuration.PlaceIndexName}' not found: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }
        catch (Amazon.LocationService.Model.ThrottlingException ex)
        {
            throw new GeocodeRateLimitException($"Amazon Location rate limit exceeded: {ex.Message}", ex)
            {
                ProviderName = Name,
                RetryAfter = TimeSpan.FromSeconds(60) // Default retry after 60 seconds
            };
        }
        catch (Amazon.LocationService.Model.AccessDeniedException ex)
        {
            throw new GeocodeAuthenticationException($"Amazon Location access denied: {ex.Message}", ex)
            {
                ProviderName = Name
            };
        }
        catch (AmazonServiceException ex)
        {
            throw new GeocodeProviderException($"Amazon Location service error: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
            };
        }
    }

    /// <inheritdoc />
    protected override async Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new DescribePlaceIndexRequest
            {
                IndexName = _configuration.PlaceIndexName
            };

            await _locationClient.DescribePlaceIndexAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Amazon.LocationService.Model.ResourceNotFoundException ex)
        {
            throw new GeocodeProviderException($"Amazon Location place index '{_configuration.PlaceIndexName}' not found: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.InvalidConfiguration
            };
        }
        catch (Amazon.LocationService.Model.AccessDeniedException ex)
        {
            throw new GeocodeAuthenticationException($"Amazon Location access denied: {ex.Message}", ex)
            {
                ProviderName = Name
            };
        }
        catch (AmazonServiceException ex)
        {
            throw new GeocodeProviderException($"Amazon Location service error: {ex.Message}", ex)
            {
                ProviderName = Name,
                ErrorCode = GeocodeErrorCodes.ServiceUnavailable
            };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposeClient)
        {
            _locationClient?.Dispose();
        }
    }

    private IAmazonLocationService CreateLocationClient(AmazonLocationProviderConfiguration configuration)
    {
        var regionEndpoint = RegionEndpoint.GetBySystemName(configuration.Region);

        if (!configuration.UseIamRole)
        {
            // Use explicit credentials
            if (string.IsNullOrWhiteSpace(configuration.AccessKeyId) || string.IsNullOrWhiteSpace(configuration.SecretAccessKey))
            {
                throw new GeocodeProviderException("Amazon Location Service access keys are required when not using IAM role")
                {
                    ProviderName = ProviderName,
                    ErrorCode = GeocodeErrorCodes.InvalidConfiguration
                };
            }

            var credentials = new BasicAWSCredentials(configuration.AccessKeyId, configuration.SecretAccessKey);
            return new AmazonLocationServiceClient(credentials, regionEndpoint);
        }

        // Use IAM role or default credential chain
        return new AmazonLocationServiceClient(regionEndpoint);
    }

    private IReadOnlyList<GeocodeCandidate> ConvertSearchResults(
        List<SearchForTextResult> results,
        ForwardGeocodeRequest request)
    {
        var candidates = new List<GeocodeCandidate>();

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];

            var attributes = BuildAttributes(
                providerId: result.PlaceId,
                additionalAttributes: new Dictionary<string, string?>
                {
                    ["relevance"] = result.Relevance.ToString(),
                    ["distance"] = result.Distance.ToString(),
                    ["interpolated"] = result.Place?.Interpolated?.ToString().ToLowerInvariant() ?? "false"
                });

            var score = CalculateSearchScore(result, i);
            var structuredAddress = ConvertPlace(result.Place);

            candidates.Add(new GeocodeCandidate(
                Address: result.Place?.Label ?? "Unknown Address",
                X: result.Place?.Geometry?.Point?[0] ?? 0,
                Y: result.Place?.Geometry?.Point?[1] ?? 0,
                Score: score,
                Attributes: attributes,
                ProviderId: result.PlaceId,
                DistanceMeters: result.Distance)
            {
                MatchLevel = result.Place?.Interpolated == true ? "interpolated" : "exact",
                AddressType = GetAddressType(result.Place?.Categories),
                SpatialReferenceWkid = request.SpatialReferenceWkid,
                StructuredAddress = structuredAddress
            });
        }

        return candidates;
    }

    private ReverseGeocodeMatch? ConvertReverseResult(
        SearchForPositionResult? result,
        ReverseGeocodeRequest request)
    {
        if (result?.Place == null)
        {
            return null;
        }

        var attributes = BuildAttributes(
            providerId: result.PlaceId,
            additionalAttributes: new Dictionary<string, string?>
            {
                ["distance"] = result.Distance.ToString(),
                ["interpolated"] = result.Place.Interpolated?.ToString().ToLowerInvariant() ?? "false"
            });

        var structuredAddress = ConvertPlace(result.Place);

        return new ReverseGeocodeMatch(
            Address: result.Place.Label ?? "Unknown Address",
            X: request.X,
            Y: request.Y,
            Attributes: attributes,
            ProviderId: result.PlaceId,
            DistanceMeters: result.Distance)
        {
            AddressType = GetAddressType(result.Place.Categories),
            SpatialReferenceWkid = request.SpatialReferenceWkid,
            StructuredAddress = structuredAddress
        };
    }

    private IReadOnlyList<GeocodeSuggestion> ConvertSuggestionResults(List<SearchForSuggestionsResult> results)
    {
        var suggestions = new List<GeocodeSuggestion>();

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];

            suggestions.Add(new GeocodeSuggestion(
                Text: result.Text,
                MagicKey: result.PlaceId ?? $"aws_suggestion_{i}",
                IsCollection: false)
            {
                Category = result.Categories?.FirstOrDefault()
            });
        }

        return suggestions;
    }

    private static double CalculateSearchScore(SearchForTextResult result, int resultIndex)
    {
        // Start with base score that decreases with position
        var baseScore = Math.Max(100 - (resultIndex * 5), 20);

        // Adjust based on relevance if available
        baseScore = (int)(baseScore * (result.Relevance ?? 1.0));

        return Math.Clamp(baseScore, 0, 100);
    }

    private static string? GetAddressType(List<string>? categories)
    {
        if (categories == null || categories.Count == 0)
        {
            return "Locality";
        }

        var category = categories.First();
        return category switch
        {
            "ADDRESS" => "PointAddress",
            "INTERPOLATED" => "StreetAddress",
            "STREET" => "StreetAddress",
            "NEIGHBORHOOD" => "Neighborhood",
            "LOCALITY" => "City",
            "SUB_REGION" => "County",
            "REGION" => "State",
            "COUNTRY" => "Country",
            "POSTAL_CODE" => "PostalCode",
            _ => "Locality"
        };
    }

    private static StructuredAddress? ConvertPlace(Place? place)
    {
        if (place == null) return null;

        return new StructuredAddress
        {
            AddressNumber = place.AddressNumber,
            StreetName = place.Street,
            City = place.Municipality,
            Region = place.Region,
            PostalCode = place.PostalCode,
            Country = place.Country,
            Neighborhood = place.Neighborhood,
            Subaddress = place.SubRegion
        };
    }
}
