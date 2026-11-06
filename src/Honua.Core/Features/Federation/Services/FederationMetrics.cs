// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Honua.Core.Features.Federation.Domain;

namespace Honua.Core.Features.Federation.Services;

public sealed class FederationMetrics : IDisposable
{
    public const string MeterName = "Honua.Federation";

    private readonly Meter _meter;

    internal Meter Meter => _meter;

    private readonly Histogram<double> _sourceDuration;
    private readonly Counter<long> _sourceRequests;
    private readonly Counter<long> _sourceRows;

    public FederationMetrics()
    {
        _meter = new Meter(MeterName);

        // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
        // Prometheus exporter derives the exported series name from the instrument name AND its unit:
        // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
        // out from under every dashboard and alert rule without breaking a single build. A PromQL query
        // against the absent name returns an empty vector, so the panel is blank and the alert never
        // fires, silently. Units are documented in the instrument name and description instead. See the
        // SLO-contract comment block in HonuaTelemetry.cs, observability/metric-name-contract.json, and
        // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

        _sourceDuration = _meter.CreateHistogram<double>(
            "honua_federation_source_duration_ms",
            unit: null,
            "Wall-clock time spent fetching candidate rows from a federated source, including resilience handling.");

        _sourceRequests = _meter.CreateCounter<long>(
            "honua_federation_source_requests_total",
            description: "Total federated source fetches, tagged by outcome (success or failure reason).");

        _sourceRows = _meter.CreateCounter<long>(
            "honua_federation_source_rows_total",
            description: "Total candidate rows returned by federated sources after local refinement.");
    }

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

    public void RecordUnavailable(FederatedSourceDescriptor source, FederatedSourceUnavailableReason reason, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tags = BuildTags(source, OutcomeFor(reason));
        _sourceDuration.Record(elapsed.TotalMilliseconds, tags);
        _sourceRequests.Add(1, tags);
    }

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
