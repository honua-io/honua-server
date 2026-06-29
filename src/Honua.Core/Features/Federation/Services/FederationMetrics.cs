// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.Federation.Domain;

namespace Honua.Core.Features.Federation.Services;

/// <summary>
/// Per-source latency and error metrics for federated query execution (issue #341
/// acceptance criterion: "Latency and error metrics per federated source"). Every remote
/// fetch the <see cref="FederatedQueryExecutor"/> performs records one duration sample and
/// one request count, tagged with the source identifier, transport kind, and the outcome
/// (success or the failure reason), so operators can see which federated source is slow or
/// failing and confirm the circuit breaker is shedding load rather than cascading.
/// </summary>
public sealed class FederationMetrics : IDisposable
{
    /// <summary>
    /// The <see cref="Meter"/> name emitted for OpenTelemetry. Registered in the service
    /// defaults meter allow-list so the federation metrics are exported by default.
    /// </summary>
    public const string MeterName = "Honua.Federation";

    private readonly Meter _meter;
    private readonly Histogram<double> _sourceDuration;
    private readonly Counter<long> _sourceRequests;
    private readonly Counter<long> _sourceRows;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationMetrics"/> class.
    /// </summary>
    public FederationMetrics()
    {
        _meter = new Meter(MeterName);

        _sourceDuration = _meter.CreateHistogram<double>(
            "honua_federation_source_duration_ms",
            "milliseconds",
            "Wall-clock time spent fetching candidate rows from a federated source, including resilience handling.");

        _sourceRequests = _meter.CreateCounter<long>(
            "honua_federation_source_requests_total",
            description: "Total federated source fetches, tagged by outcome (success or failure reason).");

        _sourceRows = _meter.CreateCounter<long>(
            "honua_federation_source_rows_total",
            description: "Total candidate rows returned by federated sources after local refinement.");
    }

    /// <summary>
    /// Records a successful federated source fetch.
    /// </summary>
    /// <param name="source">The source that was queried.</param>
    /// <param name="elapsed">The wall-clock time spent on the fetch.</param>
    /// <param name="rowCount">The number of rows returned after local refinement.</param>
    public void RecordSuccess(FederatedSourceDescriptor source, TimeSpan elapsed, int rowCount)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tags = BuildTags(source, "success");
        _sourceDuration.Record(elapsed.TotalMilliseconds, tags);
        _sourceRequests.Add(1, tags);
        if (rowCount > 0)
        {
            _sourceRows.Add(rowCount, tags);
        }
    }

    /// <summary>
    /// Records a federated source fetch that failed because the source was unavailable.
    /// </summary>
    /// <param name="source">The source that was queried.</param>
    /// <param name="reason">Why the source was unavailable.</param>
    /// <param name="elapsed">The wall-clock time spent before the failure surfaced.</param>
    public void RecordUnavailable(FederatedSourceDescriptor source, FederatedSourceUnavailableReason reason, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tags = BuildTags(source, OutcomeFor(reason));
        _sourceDuration.Record(elapsed.TotalMilliseconds, tags);
        _sourceRequests.Add(1, tags);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static TagList BuildTags(FederatedSourceDescriptor source, string outcome) => new()
    {
        { "source.id", source.Id },
        { "source.kind", source.Kind.ToString() },
        { "outcome", outcome },
    };

    private static string OutcomeFor(FederatedSourceUnavailableReason reason) => reason switch
    {
        FederatedSourceUnavailableReason.TimedOut => "timed_out",
        FederatedSourceUnavailableReason.CircuitOpen => "circuit_open",
        FederatedSourceUnavailableReason.NoConnector => "no_connector",
        FederatedSourceUnavailableReason.Faulted => "faulted",
        _ => "faulted",
    };
}
