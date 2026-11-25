// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

internal enum OtlpProbeState
{
    Unknown = 0,

    Disabled = 1,

    Healthy = 2,

    Unreachable = 3,

    AuthFailure = 4,
}

internal sealed record OtlpProbeSnapshot
{
    public required OtlpProbeState State { get; init; }

    public DateTimeOffset? ProbedAt { get; init; }

    public string? LastError { get; init; }
}

internal sealed class OtlpProbeDependencies
{
    public required IOptions<TracingOptions> TracingOptions { get; init; }

    public required IConfiguration Configuration { get; init; }

    public required IHttpClientFactory HttpClientFactory { get; init; }

    public TimeProvider? TimeProvider { get; init; }

    public ILogger<BackgroundOtlpProbe>? Logger { get; init; }
}

internal sealed partial class BackgroundOtlpProbe : BackgroundService
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    private const int ProbeTimeoutSeconds = 5;

    private readonly IOptions<TracingOptions> _tracingOptions;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly ILogger<BackgroundOtlpProbe>? _logger;

    private OtlpProbeSnapshot _snapshot = new() { State = OtlpProbeState.Unknown };

    public BackgroundOtlpProbe(OtlpProbeDependencies dependencies)
        : this(dependencies, DefaultInterval)
    {
    }

    internal BackgroundOtlpProbe(OtlpProbeDependencies dependencies, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _tracingOptions = dependencies.TracingOptions ?? throw new ArgumentNullException(nameof(dependencies));
        _configuration = dependencies.Configuration ?? throw new ArgumentNullException(nameof(dependencies));
        _httpClientFactory = dependencies.HttpClientFactory ?? throw new ArgumentNullException(nameof(dependencies));
        _timeProvider = dependencies.TimeProvider ?? TimeProvider.System;
        _interval = interval > TimeSpan.Zero ? interval : DefaultInterval;
        _logger = dependencies.Logger;
    }

    public const string HttpClientName = "honua-otlp-probe";

    public OtlpProbeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First probe runs after one interval to avoid blocking startup.
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunProbeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (_logger is not null)
                    {
                        LogProbeFailed(_logger, ex);
                    }

                    Volatile.Write(ref _snapshot, new OtlpProbeSnapshot
                    {
                        State = OtlpProbeState.Unreachable,
                        ProbedAt = _timeProvider.GetUtcNow(),
                        LastError = "OTLP endpoint probe failed."
                    });
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    [LoggerMessage(
        EventId = 8620,
        Level = LogLevel.Warning,
        Message = "Background OTLP probe iteration failed; continuing on next interval.")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception);

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
