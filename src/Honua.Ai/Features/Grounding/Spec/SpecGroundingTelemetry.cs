// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Ai.Grounding.Spec;

internal static class SpecGroundingTelemetry
{
    private static readonly Counter<long> MutateTurns = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.grounding.spec.mutate.turns");
    private static readonly Counter<long> Mutations = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.grounding.spec.mutate.mutations");
    private static readonly Counter<long> ValidationFailures = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.grounding.spec.validation_failure");
    private static readonly Counter<long> Summaries = HonuaTelemetry.Meter.CreateCounter<long>(
        "honua.grounding.spec.summarize.count");
    // `unit: null` on the instruments below is deliberate, not an oversight. The OpenTelemetry
    // Prometheus exporter derives the exported series name from the instrument name AND its unit:
    // it maps the unit through the UCUM table and appends it, so a declared unit renames the series
    // out from under every dashboard and alert rule without breaking a single build. A PromQL query
    // against the absent name returns an empty vector, so the panel is blank and the alert never
    // fires, silently. Units are documented in the instrument name and description instead. See the
    // SLO-contract comment block in HonuaTelemetry.cs, observability/metric-name-contract.json, and
    // MetricNameContractTests, which scrapes the real /metrics exposition and fails on drift.

    private static readonly Histogram<double> SummaryLatency = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.grounding.spec.summarize.latency",
        unit: null,
        description: "Spec-grounding summarize latency in milliseconds.");

    public static void RecordMutateTurn(
        bool clarified,
        bool retried,
        string? errorKind)
    {
        MutateTurns.Add(
            1,
            new KeyValuePair<string, object?>("clarified", clarified),
            new KeyValuePair<string, object?>("retried", retried),
            new KeyValuePair<string, object?>("error", errorKind ?? string.Empty));
    }

    public static void RecordMutationKinds(
        IEnumerable<SpecMutation> mutations,
        IEnumerable<string> touchedSections)
    {
        var sections = touchedSections.ToArray();
        foreach (var mutation in mutations)
        {
            var section = sections.Length == 1 ? sections[0] : string.Empty;
            Mutations.Add(
                1,
                new KeyValuePair<string, object?>("mutation_kind", mutation.Kind.ToString().ToLowerInvariant()),
                new KeyValuePair<string, object?>("section", section));
        }
    }

    public static void RecordValidationFailures(IEnumerable<string> codes)
    {
        foreach (var code in codes)
        {
            ValidationFailures.Add(
                1,
                new KeyValuePair<string, object?>("diagnostic_code", code));
        }
    }

    public static void RecordSummary(int sectionCount, double durationMs)
    {
        Summaries.Add(1, new KeyValuePair<string, object?>("cached", false));
        SummaryLatency.Record(durationMs, new KeyValuePair<string, object?>("sections", sectionCount));
    }
}
