// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Geocoding.Services;

/// <summary>
/// Default implementation of the geocoding coordinator service
/// </summary>
internal sealed class GeocodeCoordinatorService : IGeocodeCoordinatorService
{
    private readonly IGeocodeProviderRegistry _providerRegistry;
    private readonly GeocodingConfiguration _configuration;
    private readonly ILogger<GeocodeCoordinatorService> _logger;

    public GeocodeCoordinatorService(
        IGeocodeProviderRegistry providerRegistry,
        IOptions<GeocodingConfiguration> configuration,
        ILogger<GeocodeCoordinatorService> logger)
    {
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _configuration = configuration?.Value ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providers = GetProvidersToTry(providerName);
        var attemptedProviders = new List<string>();
        Exception? lastException = null;

        foreach (var provider in providers)
        {
            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (!provider.Capabilities.SupportsForwardGeocode)
                {
                    _logger.LogDebug("Provider {ProviderName} does not support forward geocoding", provider.Name);
                    continue;
                }

                var results = await provider.ForwardGeocodeAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogDebug(
                    "Forward geocoding completed successfully with provider {ProviderName} in {ElapsedMs}ms, returned {ResultCount} results",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds, results.Count);

                return GeocodeResult<IReadOnlyList<GeocodeCandidate>>.Success(
                    results, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                lastException = ex;

                _logger.LogWarning(ex,
                    "Forward geocoding failed with provider {ProviderName} after {ElapsedMs}ms",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds);

                if (!_configuration.EnableFailover || attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        var errorMessage = lastException?.Message ?? "No providers available for forward geocoding";
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        _logger.LogError(lastException,
            "All forward geocoding attempts failed. Attempted providers: {AttemptedProviders}",
            string.Join(", ", attemptedProviders));

        return GeocodeResult<IReadOnlyList<GeocodeCandidate>>.Failure(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    public async Task<GeocodeResult<ReverseGeocodeMatch?>> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providers = GetProvidersToTry(providerName);
        var attemptedProviders = new List<string>();
        Exception? lastException = null;

        foreach (var provider in providers)
        {
            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (!provider.Capabilities.SupportsReverseGeocode)
                {
                    _logger.LogDebug("Provider {ProviderName} does not support reverse geocoding", provider.Name);
                    continue;
                }

                var result = await provider.ReverseGeocodeAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogDebug(
                    "Reverse geocoding completed with provider {ProviderName} in {ElapsedMs}ms, found match: {HasMatch}",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds, result != null);

                return GeocodeResult<ReverseGeocodeMatch?>.Success(
                    result, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                lastException = ex;

                _logger.LogWarning(ex,
                    "Reverse geocoding failed with provider {ProviderName} after {ElapsedMs}ms",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds);

                if (!_configuration.EnableFailover || attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        var errorMessage = lastException?.Message ?? "No providers available for reverse geocoding";
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        _logger.LogError(lastException,
            "All reverse geocoding attempts failed. Attempted providers: {AttemptedProviders}",
            string.Join(", ", attemptedProviders));

        return GeocodeResult<ReverseGeocodeMatch?>.Failure(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    public async Task<GeocodeResult<IReadOnlyList<GeocodeSuggestion>>> SuggestAsync(
        SuggestGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providers = GetProvidersToTry(providerName);
        var attemptedProviders = new List<string>();
        Exception? lastException = null;

        foreach (var provider in providers)
        {
            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (!provider.Capabilities.SupportsSuggest)
                {
                    _logger.LogDebug("Provider {ProviderName} does not support suggestions", provider.Name);
                    continue;
                }

                var results = await provider.SuggestAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogDebug(
                    "Suggest completed successfully with provider {ProviderName} in {ElapsedMs}ms, returned {ResultCount} suggestions",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds, results.Count);

                return GeocodeResult<IReadOnlyList<GeocodeSuggestion>>.Success(
                    results, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                lastException = ex;

                _logger.LogWarning(ex,
                    "Suggest failed with provider {ProviderName} after {ElapsedMs}ms",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds);

                if (!_configuration.EnableFailover || attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        var errorMessage = lastException?.Message ?? "No providers available for suggestions";
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        _logger.LogError(lastException,
            "All suggest attempts failed. Attempted providers: {AttemptedProviders}",
            string.Join(", ", attemptedProviders));

        return GeocodeResult<IReadOnlyList<GeocodeSuggestion>>.Failure(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    public async Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providers = GetProvidersToTry(providerName);
        var attemptedProviders = new List<string>();
        Exception? lastException = null;

        foreach (var provider in providers)
        {
            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (!provider.Capabilities.SupportsBatch)
                {
                    _logger.LogDebug("Provider {ProviderName} does not support batch geocoding", provider.Name);
                    continue;
                }

                var results = await provider.BatchGeocodeAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogDebug(
                    "Batch geocoding completed successfully with provider {ProviderName} in {ElapsedMs}ms, returned {ResultCount} results",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds, results.Count);

                return GeocodeResult<IReadOnlyList<GeocodeCandidate>>.Success(
                    results, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                lastException = ex;

                _logger.LogWarning(ex,
                    "Batch geocoding failed with provider {ProviderName} after {ElapsedMs}ms",
                    provider.Name, stopwatch.Elapsed.TotalMilliseconds);

                if (!_configuration.EnableFailover || attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        var errorMessage = lastException?.Message ?? "No providers available for batch geocoding";
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        _logger.LogError(lastException,
            "All batch geocoding attempts failed. Attempted providers: {AttemptedProviders}",
            string.Join(", ", attemptedProviders));

        return GeocodeResult<IReadOnlyList<GeocodeCandidate>>.Failure(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    private IReadOnlyList<IGeocodeProvider> GetProvidersToTry(string? preferredProviderName)
    {
        var providers = new List<IGeocodeProvider>();

        // Add preferred provider first if specified and available
        if (!string.IsNullOrWhiteSpace(preferredProviderName))
        {
            var preferredProvider = _providerRegistry.GetProvider(preferredProviderName);
            if (preferredProvider != null)
            {
                providers.Add(preferredProvider);
            }
            else
            {
                _logger.LogWarning("Preferred provider {ProviderName} is not available", preferredProviderName);
            }
        }

        // Add default provider if not already added
        if (providers.Count == 0 || !providers.Any(p => p.Name.Equals(_configuration.DefaultProvider, StringComparison.OrdinalIgnoreCase)))
        {
            var defaultProvider = _providerRegistry.GetProvider(_configuration.DefaultProvider);
            if (defaultProvider != null)
            {
                providers.Add(defaultProvider);
            }
        }

        // Add other providers for failover if enabled
        if (_configuration.EnableFailover && providers.Count < _configuration.MaxFailoverAttempts)
        {
            var allProviders = _providerRegistry.GetAllProviders();
            var additionalProviders = allProviders
                .Where(p => !providers.Any(existing => existing.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                .Take(_configuration.MaxFailoverAttempts - providers.Count);

            providers.AddRange(additionalProviders);
        }

        return providers;
    }
}

/// <summary>
/// Provider coordinator that maintains backward compatibility with the existing interface
/// </summary>
internal sealed class GeocodeProviderCoordinator : IGeocodeProviderCoordinator
{
    private readonly IGeocodeCoordinatorService _coordinatorService;
    private readonly IGeocodeProviderRegistry _providerRegistry;

    public GeocodeProviderCoordinator(
        IGeocodeCoordinatorService coordinatorService,
        IGeocodeProviderRegistry providerRegistry)
    {
        _coordinatorService = coordinatorService ?? throw new ArgumentNullException(nameof(coordinatorService));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    }

    public IGeocodeProvider? GetProvider(string? providerName = null)
    {
        return string.IsNullOrWhiteSpace(providerName)
            ? _providerRegistry.GetAllProviders().FirstOrDefault()
            : _providerRegistry.GetProvider(providerName);
    }

    public IReadOnlyList<IGeocodeProvider> GetAllProviders()
    {
        return _providerRegistry.GetAllProviders();
    }

    public async Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _coordinatorService.ForwardGeocodeAsync(request, providerName, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Data : [];
    }

    public async Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _coordinatorService.ReverseGeocodeAsync(request, providerName, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Data : null;
    }

    public async Task<IReadOnlyList<GeocodeProviderHealth>> CheckAllProvidersHealthAsync(CancellationToken cancellationToken = default)
    {
        var providers = _providerRegistry.GetAllProviders();
        var healthTasks = providers.Select(p => p.CheckHealthAsync(cancellationToken));
        var healthResults = await Task.WhenAll(healthTasks).ConfigureAwait(false);
        return healthResults;
    }
}
