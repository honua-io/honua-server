// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Grounding.Spec;

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
    private static readonly Histogram<double> SummaryLatency = HonuaTelemetry.Meter.CreateHistogram<double>(
        "honua.grounding.spec.summarize.latency",
        unit: "ms");

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
