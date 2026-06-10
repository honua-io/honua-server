// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Runtime reachability state observed by <see cref="BackgroundOtlpProbe"/>.
/// </summary>
internal enum OtlpProbeState
{
    /// <summary>
    /// No probe has executed yet (typically before the first interval elapses).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Probe is disabled because no OTLP endpoint is configured.
    /// </summary>
    Disabled = 1,

    /// <summary>
    /// OTLP endpoint responded with a non-auth, non-failure response — exporter is reachable.
    /// </summary>
    Healthy = 2,

    /// <summary>
    /// OTLP endpoint did not respond (DNS failure, connection refused, timeout).
    /// </summary>
    Unreachable = 3,

    /// <summary>
    /// OTLP endpoint returned 401 or 403.
    /// </summary>
    AuthFailure = 4,
}

/// <summary>
/// Snapshot of probe state, surfaced through the admin telemetry status endpoint.
/// </summary>
internal sealed record OtlpProbeSnapshot
{
    /// <summary>
    /// Observed state at <see cref="ProbedAt"/>.
    /// </summary>
    public required OtlpProbeState State { get; init; }

    /// <summary>
    /// When the probe last attempted contact.
    /// </summary>
    public DateTimeOffset? ProbedAt { get; init; }

    /// <summary>
    /// Last sanitized error message, when state indicates a failure.
    /// </summary>
    public string? LastError { get; init; }
}

/// <summary>
/// Background probe that periodically validates OTLP endpoint reachability so
/// the admin telemetry status endpoint can distinguish <c>unreachable</c> and
/// <c>authFailure</c> from configured-but-unverified states (#1168).
/// </summary>
/// <remarks>
/// <para>
/// The probe stays off the request path — handlers read the cached snapshot
/// only. Probe interval defaults to 60 seconds and never blocks startup.
/// </para>
/// <para>
/// The probe is opt-in: when no OTLP endpoint is configured, the state stays
/// <see cref="OtlpProbeState.Disabled"/> and no outbound traffic is generated.
/// This is intentional so air-gapped deployments do not accumulate probe noise.
/// </para>
/// </remarks>
internal sealed class BackgroundOtlpProbe : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    private const int ProbeTimeoutSeconds = 5;

    private readonly IOptions<TracingOptions> _tracingOptions;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;

    private OtlpProbeSnapshot _snapshot = new() { State = OtlpProbeState.Unknown };

    public BackgroundOtlpProbe(
        IOptions<TracingOptions> tracingOptions,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider)
        : this(tracingOptions, configuration, httpClientFactory, timeProvider, DefaultInterval)
    {
    }

    internal BackgroundOtlpProbe(
        IOptions<TracingOptions> tracingOptions,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        TimeSpan interval)
    {
        _tracingOptions = tracingOptions ?? throw new ArgumentNullException(nameof(tracingOptions));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = interval > TimeSpan.Zero ? interval : DefaultInterval;
    }

    /// <summary>
    /// HTTP client name used for the probe so the configured handler / retry policy
    /// can be customized in DI without touching this class.
    /// </summary>
    public const string HttpClientName = "honua-otlp-probe";

    public OtlpProbeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal Task ProbeOnceForTestsAsync(CancellationToken cancellationToken) => RunProbeAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First probe runs after one interval to avoid blocking startup.
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunProbeAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunProbeAsync(CancellationToken cancellationToken)
    {
        var endpoint = ResolveOtlpEndpoint();
        var now = _timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Volatile.Write(ref _snapshot, new OtlpProbeSnapshot { State = OtlpProbeState.Disabled, ProbedAt = now });
            return;
        }

        using var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(ProbeTimeoutSeconds);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, uri);
            ApplyAuthHeaders(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, MapResponseToSnapshot(response, now));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _snapshot, new OtlpProbeSnapshot
            {
                State = OtlpProbeState.Unreachable,
                ProbedAt = now,
                LastError = $"timeout after {ProbeTimeoutSeconds}s"
            });
        }
        catch (HttpRequestException)
        {
            Volatile.Write(ref _snapshot, new OtlpProbeSnapshot
            {
                State = OtlpProbeState.Unreachable,
                ProbedAt = now,
                LastError = "OTLP endpoint could not be reached."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static OtlpProbeSnapshot MapResponseToSnapshot(HttpResponseMessage response, DateTimeOffset probedAt)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new OtlpProbeSnapshot
            {
                State = OtlpProbeState.AuthFailure,
                ProbedAt = probedAt,
                LastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }

        // Treat 5xx responses as unreachable — the endpoint is responding but
        // upstream is broken, so Console must not see this as Healthy.
        if ((int)response.StatusCode >= 500)
        {
            return new OtlpProbeSnapshot
            {
                State = OtlpProbeState.Unreachable,
                ProbedAt = probedAt,
                LastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }

        return new OtlpProbeSnapshot { State = OtlpProbeState.Healthy, ProbedAt = probedAt };
    }

    private string? ResolveOtlpEndpoint()
    {
        var fromOptions = _tracingOptions.Value.OtlpEndpoint;
        if (!string.IsNullOrWhiteSpace(fromOptions))
        {
            return fromOptions;
        }

        return _configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    }

    private void ApplyAuthHeaders(HttpRequestMessage request)
    {
        var headers = !string.IsNullOrWhiteSpace(_tracingOptions.Value.OtlpHeaders)
            ? _tracingOptions.Value.OtlpHeaders
            : _configuration["OTEL_EXPORTER_OTLP_HEADERS"];
        if (string.IsNullOrWhiteSpace(headers))
        {
            return;
        }

        foreach (var pair in headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = pair.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == pair.Length - 1)
            {
                continue;
            }

            var name = pair[..equalsIndex].Trim();
            var value = pair[(equalsIndex + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
