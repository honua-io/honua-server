// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Geocoding.Features.Geocoding.Services;

/// <summary>
/// Default implementation of the geocoding coordinator service
/// </summary>
internal sealed class GeocodeCoordinatorService : IGeocodeCoordinatorService
{
    private readonly IGeocodeProviderRegistry _providerRegistry;
    private readonly GeocodingConfiguration _configuration;
    private readonly IGeocodeLimitEnforcer _limitEnforcer;
    private readonly ILogger<GeocodeCoordinatorService> _logger;

    public GeocodeCoordinatorService(
        IGeocodeProviderRegistry providerRegistry,
        IOptions<GeocodingConfiguration> configuration,
        IGeocodeLimitEnforcer limitEnforcer,
        ILogger<GeocodeCoordinatorService> logger)
    {
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _configuration = configuration?.Value ?? throw new ArgumentNullException(nameof(configuration));
        _limitEnforcer = limitEnforcer ?? throw new ArgumentNullException(nameof(limitEnforcer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Checks the rate limit for a single candidate provider before it is attempted.
    /// Returns a throttle result if the provider's per-minute limit is exceeded, otherwise
    /// <see langword="null"/>. Called per-provider inside the failover loop so a throttled
    /// primary does not prevent failover to a healthy secondary (BH2-020).
    /// </summary>
    private GeocodeResult<T>? CheckProviderRateLimit<T>(IGeocodeProvider provider)
    {
        var decision = _limitEnforcer.CheckRequestRate(provider.Name, provider.Capabilities);
        if (decision.Allowed)
        {
            return null;
        }

        var retryAfterSeconds = (int)Math.Ceiling((decision.RetryAfter ?? TimeSpan.Zero).TotalSeconds);
        GeocodeCoordinatorLog.RateLimitRejected(_logger, provider.Name, decision.EffectiveLimit ?? 0, retryAfterSeconds);

        return new GeocodeResult<T>
        {
            Data = default!,
            ProviderName = provider.Name,
            IsSuccess = false,
            ErrorMessage = decision.Reason ?? "Geocoding provider rate limit was exceeded.",
            AttemptedProviders = [provider.Name],
            Metadata = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [GeocodeLimitMetadata.RetryAfterSecondsKey] = retryAfterSeconds
            }
        };
    }

    public Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
        => ForwardGeocodeAsync(request, providerName, allowFailover: false, cancellationToken);

    public async Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> ForwardGeocodeAsync(
        ForwardGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = GeocodingTelemetry.Source.StartActivity("geocoding.forwardGeocode");
        activity?.SetTag("honua.geocoding.operation", "forward");
        activity?.SetTag("honua.geocoding.preferred_provider", providerName);

        var providers = GetProvidersToTry(providerName, allowFailover);
        var unavailable = RefuseUnregisteredPreferredProvider<IReadOnlyList<GeocodeCandidate>>(providerName, providers);
        if (unavailable is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, unavailable.ErrorMessage);
            return unavailable;
        }

        var attemptedProviders = new List<string>();
        Exception? lastException = null;
        GeocodeResult<IReadOnlyList<GeocodeCandidate>>? lastThrottleResult = null;

        foreach (var provider in providers)
        {
            // A provider that cannot perform the operation is skipped before it counts against
            // the failover budget, so capability-incompatible providers never exhaust the
            // MaxFailoverAttempts allowance for a working provider later in the list.
            if (!provider.Capabilities.SupportsForwardGeocode)
            {
                GeocodeCoordinatorLog.CapabilityNotSupported(_logger, provider.Name, "forward geocoding");
                continue;
            }

            // Stop once the configured number of real attempts is reached. Capability skips above
            // are free and never consume this budget.
            if (!HasFailoverBudget(attemptedProviders.Count, allowFailover))
            {
                break;
            }

            // Per-provider rate-limit check. A throttled provider is skipped without
            // consuming the failover budget so a healthy secondary can still be reached.
            var throttleResult = CheckProviderRateLimit<IReadOnlyList<GeocodeCandidate>>(provider);
            if (throttleResult is not null)
            {
                lastThrottleResult = throttleResult;
                continue;
            }

            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var results = await provider.ForwardGeocodeAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                GeocodeCoordinatorLog.OperationCompleted(
                    _logger,
                    "forward geocoding",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    results.Count);

                activity?.SetTag("honua.geocoding.provider", provider.Name);
                activity?.SetTag("honua.geocoding.result_count", results.Count);

                return GeocodeResults.Success<IReadOnlyList<GeocodeCandidate>>(
                    results, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: this is a per-provider attempt inside the failover loop;
            // the exception is already logged with full detail below and failover to the next provider continues.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                stopwatch.Stop();
                lastException = ex;

                GeocodeCoordinatorLog.OperationFailed(
                    _logger,
                    "forward geocoding",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ex);

                if (!allowFailover ||
                    !_configuration.EnableFailover ||
                    attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        // If every capable provider was throttled and nothing was actually attempted,
        // return the last throttle result so the caller surfaces a 429.
        if (attemptedProviders.Count == 0 && lastThrottleResult is not null && lastException is null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, lastThrottleResult.ErrorMessage);
            return lastThrottleResult;
        }

        var errorMessage = BuildFailureMessage(lastException, "forward geocoding", attemptedProviders);
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        GeocodeCoordinatorLog.AllAttemptsFailed(
            _logger,
            "forward geocoding",
            string.Join(", ", attemptedProviders),
            lastException);

        activity?.SetTag("honua.geocoding.provider", failedProviderName);
        activity?.SetTag("honua.geocoding.result_count", 0);
        activity?.SetStatus(ActivityStatusCode.Error, errorMessage);

        return GeocodeResults.Failure<IReadOnlyList<GeocodeCandidate>>(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    public Task<GeocodeResult<ReverseGeocodeMatch?>> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
        => ReverseGeocodeAsync(request, providerName, allowFailover: false, cancellationToken);

    public async Task<GeocodeResult<ReverseGeocodeMatch?>> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = GeocodingTelemetry.Source.StartActivity("geocoding.reverseGeocode");
        activity?.SetTag("honua.geocoding.operation", "reverse");
        activity?.SetTag("honua.geocoding.preferred_provider", providerName);

        var providers = GetProvidersToTry(providerName, allowFailover);
        var unavailable = RefuseUnregisteredPreferredProvider<ReverseGeocodeMatch?>(providerName, providers);
        if (unavailable is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, unavailable.ErrorMessage);
            return unavailable;
        }

        var attemptedProviders = new List<string>();
        Exception? lastException = null;
        GeocodeResult<ReverseGeocodeMatch?>? lastThrottleResult = null;

        foreach (var provider in providers)
        {
            // Capability-incompatible providers are skipped before counting against the failover
            // budget so they cannot exhaust MaxFailoverAttempts ahead of a capable provider.
            if (!provider.Capabilities.SupportsReverseGeocode)
            {
                GeocodeCoordinatorLog.CapabilityNotSupported(_logger, provider.Name, "reverse geocoding");
                continue;
            }

            // Stop once the configured number of real attempts is reached. Capability skips above
            // are free and never consume this budget.
            if (!HasFailoverBudget(attemptedProviders.Count, allowFailover))
            {
                break;
            }

            // Per-provider rate-limit check. A throttled provider is skipped without
            // consuming the failover budget so a healthy secondary can still be reached.
            var throttleResult = CheckProviderRateLimit<ReverseGeocodeMatch?>(provider);
            if (throttleResult is not null)
            {
                lastThrottleResult = throttleResult;
                continue;
            }

            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await provider.ReverseGeocodeAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                GeocodeCoordinatorLog.ReverseOperationCompleted(
                    _logger,
                    "reverse geocoding",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    result != null);

                activity?.SetTag("honua.geocoding.provider", provider.Name);
                activity?.SetTag("honua.geocoding.result_count", result != null ? 1 : 0);

                return GeocodeResults.Success<ReverseGeocodeMatch?>(
                    result, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: this is a per-provider attempt inside the failover loop;
            // the exception is already logged with full detail below and failover to the next provider continues.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                stopwatch.Stop();
                lastException = ex;

                GeocodeCoordinatorLog.OperationFailed(
                    _logger,
                    "reverse geocoding",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ex);

                if (!allowFailover ||
                    !_configuration.EnableFailover ||
                    attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        // If every capable provider was throttled and nothing was actually attempted,
        // return the last throttle result so the caller surfaces a 429.
        if (attemptedProviders.Count == 0 && lastThrottleResult is not null && lastException is null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, lastThrottleResult.ErrorMessage);
            return lastThrottleResult;
        }

        var errorMessage = BuildFailureMessage(lastException, "reverse geocoding", attemptedProviders);
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        GeocodeCoordinatorLog.AllAttemptsFailed(
            _logger,
            "reverse geocoding",
            string.Join(", ", attemptedProviders),
            lastException);

        activity?.SetTag("honua.geocoding.provider", failedProviderName);
        activity?.SetTag("honua.geocoding.result_count", 0);
        activity?.SetStatus(ActivityStatusCode.Error, errorMessage);

        return GeocodeResults.Failure<ReverseGeocodeMatch?>(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    public Task<GeocodeResult<IReadOnlyList<GeocodeSuggestion>>> SuggestAsync(
        SuggestGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
        => SuggestAsync(request, providerName, allowFailover: false, cancellationToken);

    public async Task<GeocodeResult<IReadOnlyList<GeocodeSuggestion>>> SuggestAsync(
        SuggestGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = GeocodingTelemetry.Source.StartActivity("geocoding.suggest");
        activity?.SetTag("honua.geocoding.operation", "suggest");
        activity?.SetTag("honua.geocoding.preferred_provider", providerName);
        // Length only — never tag the raw query text (PII).
        activity?.SetTag("honua.geocoding.query_length", request.Text?.Length ?? 0);

        var providers = GetProvidersToTry(providerName, allowFailover);
        var unavailable = RefuseUnregisteredPreferredProvider<IReadOnlyList<GeocodeSuggestion>>(providerName, providers);
        if (unavailable is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, unavailable.ErrorMessage);
            return unavailable;
        }

        var attemptedProviders = new List<string>();
        Exception? lastException = null;
        GeocodeResult<IReadOnlyList<GeocodeSuggestion>>? lastThrottleResult = null;

        foreach (var provider in providers)
        {
            // Capability-incompatible providers are skipped before counting against the failover
            // budget so they cannot exhaust MaxFailoverAttempts ahead of a capable provider.
            if (!provider.Capabilities.SupportsSuggest)
            {
                GeocodeCoordinatorLog.CapabilityNotSupported(_logger, provider.Name, "suggest");
                continue;
            }

            // Stop once the configured number of real attempts is reached. Capability skips above
            // are free and never consume this budget.
            if (!HasFailoverBudget(attemptedProviders.Count, allowFailover))
            {
                break;
            }

            // Per-provider rate-limit check. A throttled provider is skipped without
            // consuming the failover budget so a healthy secondary can still be reached.
            var throttleResult = CheckProviderRateLimit<IReadOnlyList<GeocodeSuggestion>>(provider);
            if (throttleResult is not null)
            {
                lastThrottleResult = throttleResult;
                continue;
            }

            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var results = await provider.SuggestAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                GeocodeCoordinatorLog.OperationCompleted(
                    _logger,
                    "suggest",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    results.Count);

                activity?.SetTag("honua.geocoding.provider", provider.Name);
                activity?.SetTag("honua.geocoding.result_count", results.Count);

                return GeocodeResults.Success<IReadOnlyList<GeocodeSuggestion>>(
                    results, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: this is a per-provider attempt inside the failover loop;
            // the exception is already logged with full detail below and failover to the next provider continues.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                stopwatch.Stop();
                lastException = ex;

                GeocodeCoordinatorLog.OperationFailed(
                    _logger,
                    "suggest",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ex);

                if (!allowFailover ||
                    !_configuration.EnableFailover ||
                    attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        // If every capable provider was throttled and nothing was actually attempted,
        // return the last throttle result so the caller surfaces a 429.
        if (attemptedProviders.Count == 0 && lastThrottleResult is not null && lastException is null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, lastThrottleResult.ErrorMessage);
            return lastThrottleResult;
        }

        var errorMessage = BuildFailureMessage(lastException, "suggestions", attemptedProviders);
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        GeocodeCoordinatorLog.AllAttemptsFailed(
            _logger,
            "suggest",
            string.Join(", ", attemptedProviders),
            lastException);

        activity?.SetTag("honua.geocoding.provider", failedProviderName);
        activity?.SetTag("honua.geocoding.result_count", 0);
        activity?.SetStatus(ActivityStatusCode.Error, errorMessage);

        return GeocodeResults.Failure<IReadOnlyList<GeocodeSuggestion>>(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    public Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
        => BatchGeocodeAsync(request, providerName, allowFailover: false, cancellationToken);

    public async Task<GeocodeResult<IReadOnlyList<GeocodeCandidate>>> BatchGeocodeAsync(
        BatchGeocodeRequest request,
        string? providerName,
        bool allowFailover,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = GeocodingTelemetry.Source.StartActivity("geocoding.batch");
        activity?.SetTag("honua.geocoding.operation", "batch");
        activity?.SetTag("honua.geocoding.preferred_provider", providerName);
        activity?.SetTag("honua.geocoding.batch_size", request.Queries.Count);

        var providers = GetProvidersToTry(providerName, allowFailover);
        var unavailable = RefuseUnregisteredPreferredProvider<IReadOnlyList<GeocodeCandidate>>(providerName, providers);
        if (unavailable is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, unavailable.ErrorMessage);
            return unavailable;
        }

        // Enforce the batch cap (provider + licensing) against the effective batch-capable provider
        // before any work, then the per-minute request rate. Both reuse the shared limit enforcer so
        // advertised limits stay consistent across protocol adapters and this canonical pipeline.
        var batchPrimary = providers.FirstOrDefault(static p => p.Capabilities.SupportsBatch);
        if (batchPrimary is not null)
        {
            var batchDecision = _limitEnforcer.CheckBatch(batchPrimary.Name, batchPrimary.Capabilities, request.Queries.Count);
            if (!batchDecision.Allowed)
            {
                GeocodeCoordinatorLog.BatchSizeRejected(_logger, batchPrimary.Name, request.Queries.Count, batchDecision.EffectiveLimit ?? 0);
                var rejection = batchDecision.Reason ?? "Batch size exceeds the maximum allowed batch size.";
                activity?.SetStatus(ActivityStatusCode.Error, rejection);
                return GeocodeResults.Failure<IReadOnlyList<GeocodeCandidate>>(
                    rejection,
                    batchPrimary.Name,
                    attemptedProviders: [batchPrimary.Name]);
            }
        }

        var attemptedProviders = new List<string>();
        Exception? lastException = null;
        GeocodeResult<IReadOnlyList<GeocodeCandidate>>? lastThrottleResult = null;
        GeocodeLimitDecision? lastBatchRejection = null;
        string? lastBatchRejectionProvider = null;

        foreach (var provider in providers)
        {
            // Capability-incompatible providers are skipped before counting against the failover
            // budget so they cannot exhaust MaxFailoverAttempts ahead of a capable provider.
            if (!provider.Capabilities.SupportsBatch)
            {
                GeocodeCoordinatorLog.CapabilityNotSupported(_logger, provider.Name, "batch geocoding");
                continue;
            }

            // Stop once the configured number of real attempts is reached. Capability skips above
            // are free and never consume this budget.
            if (!HasFailoverBudget(attemptedProviders.Count, allowFailover))
            {
                break;
            }

            // Each provider's own cap, not only the first batch-capable provider. An over-cap
            // candidate is skipped without a call and without consuming the failover budget, so
            // a later provider with a larger cap can still run (#6160).
            var providerBatchDecision = _limitEnforcer.CheckBatch(
                provider.Name, provider.Capabilities, request.Queries.Count);
            if (!providerBatchDecision.Allowed)
            {
                GeocodeCoordinatorLog.BatchSizeRejected(
                    _logger,
                    provider.Name,
                    request.Queries.Count,
                    providerBatchDecision.EffectiveLimit ?? 0);
                lastBatchRejection = providerBatchDecision;
                lastBatchRejectionProvider = provider.Name;
                continue;
            }

            // Per-provider rate-limit check. A throttled provider is skipped without
            // consuming the failover budget so a healthy secondary can still be reached.
            var throttleResult = CheckProviderRateLimit<IReadOnlyList<GeocodeCandidate>>(provider);
            if (throttleResult is not null)
            {
                lastThrottleResult = throttleResult;
                continue;
            }

            attemptedProviders.Add(provider.Name);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var results = await provider.BatchGeocodeAsync(request, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                GeocodeCoordinatorLog.OperationCompleted(
                    _logger,
                    "batch geocoding",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    results.Count);

                activity?.SetTag("honua.geocoding.provider", provider.Name);
                activity?.SetTag("honua.geocoding.result_count", results.Count);

                return GeocodeResults.Success<IReadOnlyList<GeocodeCandidate>>(
                    results, provider.Name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: this is a per-provider attempt inside the failover loop;
            // the exception is already logged with full detail below and failover to the next provider continues.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                stopwatch.Stop();
                lastException = ex;

                GeocodeCoordinatorLog.OperationFailed(
                    _logger,
                    "batch geocoding",
                    provider.Name,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ex);

                if (!allowFailover ||
                    !_configuration.EnableFailover ||
                    attemptedProviders.Count >= _configuration.MaxFailoverAttempts)
                {
                    break;
                }
            }
        }

        // If every capable provider was throttled and nothing was actually attempted,
        // return the last throttle result so the caller surfaces a 429.
        if (attemptedProviders.Count == 0 && lastThrottleResult is not null && lastException is null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, lastThrottleResult.ErrorMessage);
            return lastThrottleResult;
        }

        // A later provider that refused the batch size wins over an earlier provider's
        // unrelated failure, and over "no provider supports batch" when nobody was called.
        if (lastBatchRejection is not null && (lastException is not null || attemptedProviders.Count == 0))
        {
            var rejection = lastBatchRejection.Reason ?? "Batch size exceeds the maximum allowed batch size.";
            activity?.SetStatus(ActivityStatusCode.Error, rejection);
            return GeocodeResults.Failure<IReadOnlyList<GeocodeCandidate>>(
                rejection,
                lastBatchRejectionProvider ?? "unknown",
                attemptedProviders: attemptedProviders);
        }

        var errorMessage = BuildFailureMessage(lastException, "batch geocoding", attemptedProviders);
        var failedProviderName = attemptedProviders.LastOrDefault() ?? "unknown";

        GeocodeCoordinatorLog.AllAttemptsFailed(
            _logger,
            "batch geocoding",
            string.Join(", ", attemptedProviders),
            lastException);

        activity?.SetTag("honua.geocoding.provider", failedProviderName);
        activity?.SetTag("honua.geocoding.result_count", 0);
        activity?.SetStatus(ActivityStatusCode.Error, errorMessage);

        return GeocodeResults.Failure<IReadOnlyList<GeocodeCandidate>>(
            errorMessage, failedProviderName, attemptedProviders: attemptedProviders);
    }

    // Returns true while there is remaining failover budget to initiate another real attempt.
    // Capability-skipped providers do not count toward attemptedProviders, so they never consume
    // this budget; only providers that actually attempt the operation do.
    private bool HasFailoverBudget(int attemptedCount, bool allowFailover)
    {
        if (attemptedCount == 0)
        {
            return true;
        }

        return allowFailover &&
            _configuration.EnableFailover &&
            attemptedCount < Math.Max(1, _configuration.MaxFailoverAttempts);
    }

    // An explicitly named provider is the only candidate. Substituting the default
    // (public Nominatim) when that provider is missing or down sends the caller's
    // address to a service they did not choose (#6601).
    private static GeocodeResult<T>? RefuseUnregisteredPreferredProvider<T>(
        string? providerName,
        List<IGeocodeProvider> providers)
    {
        if (string.IsNullOrWhiteSpace(providerName) || providers.Count > 0)
        {
            return null;
        }

        return new GeocodeResult<T>
        {
            Data = default!,
            ProviderName = providerName,
            IsSuccess = false,
            ErrorMessage = $"provider_unavailable: geocoding provider '{providerName}' is not registered.",
            AttemptedProviders = [],
            Metadata = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["errorCode"] = GeocodeErrorCodes.ProviderUnavailable
            }
        };
    }

    private List<IGeocodeProvider> GetProvidersToTry(string? preferredProviderName, bool allowFailover)
    {
        // Explicit requests never fail over. The unnamed path below is intentionally
        // not truncated to MaxFailoverAttempts; the calling loop enforces that budget
        // (see HasFailoverBudget) so a capability skip cannot crowd out a capable provider.
        if (!string.IsNullOrWhiteSpace(preferredProviderName))
        {
            var preferredProvider = _providerRegistry.GetProvider(preferredProviderName);
            if (preferredProvider is not null)
            {
                return [preferredProvider];
            }

            GeocodeCoordinatorLog.PreferredProviderUnavailable(_logger, preferredProviderName);
            return [];
        }

        var providers = new List<IGeocodeProvider>();
        var defaultProvider = _providerRegistry.GetProvider(_configuration.DefaultProvider);
        if (defaultProvider is not null)
        {
            providers.Add(defaultProvider);
        }

        if (allowFailover && _configuration.EnableFailover)
        {
            var additionalProviders = _providerRegistry.GetAllProviders()
                .Where(provider => !providers.Any(existing =>
                    existing.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase)));
            providers.AddRange(additionalProviders);
        }

        return providers;
    }

    private static string BuildFailureMessage(
        Exception? exception,
        string operation,
        List<string> attemptedProviders)
    {
        if (exception is null)
        {
            // No provider actually attempted the operation. Either none were registered/available,
            // or every candidate provider was skipped because it does not support this capability.
            return attemptedProviders.Count == 0
                ? $"No provider supports {operation}."
                : $"No providers available for {operation}.";
        }

        return exception switch
        {
            GeocodeRequestException requestException => requestException.Message,
            GeocodeRateLimitException => "Geocoding provider rate limit was exceeded.",
            GeocodeAuthenticationException => "Geocoding provider authentication failed.",
            _ => $"Geocoding provider failed while performing {operation}."
        };
    }
}

/// <summary>
/// Provider coordinator that maintains backward compatibility with the existing interface
/// </summary>
internal sealed class GeocodeProviderCoordinator : IGeocodeProviderCoordinator
{
    private readonly IGeocodeCoordinatorService _coordinatorService;
    private readonly IGeocodeProviderRegistry _providerRegistry;
    private readonly ILicenseEntitlementService? _entitlementService;

    public GeocodeProviderCoordinator(
        IGeocodeCoordinatorService coordinatorService,
        IGeocodeProviderRegistry providerRegistry,
        ILicenseEntitlementService? entitlementService = null)
    {
        _coordinatorService = coordinatorService ?? throw new ArgumentNullException(nameof(coordinatorService));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _entitlementService = entitlementService;
    }

    public IGeocodeProvider? GetProvider(string? providerName = null)
    {
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            return _providerRegistry.GetProvider(providerName);
        }

        var providers = _providerRegistry.GetAllProviders();
        return providers.Count > 0 ? providers[0] : null;
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
        var result = await _coordinatorService.ForwardGeocodeAsync(
            request,
            providerName,
            IsFailoverEntitled(),
            cancellationToken).ConfigureAwait(false);

        // A successful result with zero candidates is a genuine "no match" and is
        // returned as an empty list. A failure means every attempted provider errored
        // (401/429/timeout/etc.); surfacing that as an empty list would be
        // indistinguishable from a real no-match, so throw instead.
        if (!result.IsSuccess)
        {
            throw CreateProviderFailure(result, "forward geocoding");
        }

        return result.Data;
    }

    public async Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
        ReverseGeocodeRequest request,
        string? providerName = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _coordinatorService.ReverseGeocodeAsync(
            request,
            providerName,
            IsFailoverEntitled(),
            cancellationToken).ConfigureAwait(false);

        // A successful result with a null match is a genuine "no match". A failure means
        // every attempted provider errored, which must not be collapsed into null (that
        // would let callers persist NULL coordinates as if the location were genuinely
        // ungeocodable), so throw instead.
        if (!result.IsSuccess)
        {
            throw CreateProviderFailure(result, "reverse geocoding");
        }

        return result.Data;
    }

    private static GeocodeProviderException CreateProviderFailure<T>(GeocodeResult<T> result, string operation)
    {
        var baseMessage = result.ErrorMessage ?? $"All geocoding providers failed during {operation}.";
        var message = result.AttemptedProviders is { Count: > 0 } attempted
            ? $"{baseMessage} (attempted providers: {string.Join(", ", attempted)})"
            : baseMessage;

        return new GeocodeProviderException(message)
        {
            ProviderName = result.ProviderName,
            ErrorCode = GeocodeErrorCodes.ServiceUnavailable,
        };
    }

    private bool IsFailoverEntitled()
        => _entitlementService?
            .CheckEntitlement(FeatureCatalog.GeocodingFailoverKey)
            .IsActive == true;

    public async Task<IReadOnlyList<GeocodeProviderHealth>> CheckAllProvidersHealthAsync(CancellationToken cancellationToken = default)
    {
        var providers = _providerRegistry.GetAllProviders();
        var healthTasks = providers.Select(p => p.CheckHealthAsync(cancellationToken));
        var healthResults = await Task.WhenAll(healthTasks).ConfigureAwait(false);
        return healthResults;
    }
}
