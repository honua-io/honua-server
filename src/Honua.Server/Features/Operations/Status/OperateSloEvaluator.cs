// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ServiceDefaults;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// Projects the replica-local serving-latency reservoir as an explicitly non-authoritative
/// diagnostic. A distributed platform SLO is intentionally not fabricated from this source.
/// </summary>
/// <remarks>
/// The retained tail is bounded per protocol, resets with the process, and counts HTTP 5xx only. Its
/// population and effective interval are exposed so callers can use it for node diagnosis without
/// mistaking it for the all-request, cross-replica, in-band-aware release SLI.
/// </remarks>
internal static class OperateSloEvaluator
{
    internal const string DiagnosticSource = "node-local-retained-tail(http-5xx-only)";

    /// <summary>
    /// Projects a serving-latency snapshot as a node-local retained-tail diagnostic.
    /// </summary>
    /// <param name="options">The SLO configuration.</param>
    /// <param name="snapshot">The in-process serving-latency snapshot.</param>
    /// <returns>The explicit unavailable platform SLO state plus retained-tail diagnostic.</returns>
    public static OperateSloView Evaluate(OperateSloOptions options, ServingLatencySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshot);

        long requestCount = 0;
        long errorCount = 0;
        long totalRecorded = 0;
        long overwritten = 0;
        foreach (var protocol in snapshot.Protocols)
        {
            requestCount += protocol.RequestCount;
            errorCount += protocol.ErrorCount;
            totalRecorded += protocol.TotalRecordedSinceReset;
            overwritten += protocol.OverwrittenSampleCount;
        }

        // A protocol that served traffic and then idled past the window retains nothing but is not
        // a freshly reset process. Its lifetime counters are carried here so the diagnostic never
        // reports a quiet replica with the same numbers as a restarted one.
        foreach (var protocol in snapshot.IdleProtocols)
        {
            totalRecorded += protocol.TotalRecordedSinceReset;
            overwritten += protocol.OverwrittenSampleCount;
        }

        var retaining = snapshot.Protocols.Select(protocol => new OperateNodeLocalProtocolTailView
        {
            Protocol = protocol.Protocol,
            RetainedRequestCount = protocol.RequestCount,
            RetentionCapacity = protocol.RetentionCapacity,
            TotalRecordedSinceReset = protocol.TotalRecordedSinceReset,
            OverwrittenSampleCount = protocol.OverwrittenSampleCount,
            OldestRetainedSampleAgeSeconds = protocol.OldestRetainedSampleAgeSeconds,
            NewestRetainedSampleAgeSeconds = protocol.NewestRetainedSampleAgeSeconds,
        }).ToList();

        // Idle protocols publish their lifetime counters with no retained population and no ages,
        // so they never contribute fabricated latency or a misleading "newest sample" age.
        var protocols = retaining
            .Concat(snapshot.IdleProtocols.Select(protocol => new OperateNodeLocalProtocolTailView
            {
                Protocol = protocol.Protocol,
                RetainedRequestCount = 0,
                RetentionCapacity = protocol.RetentionCapacity,
                TotalRecordedSinceReset = protocol.TotalRecordedSinceReset,
                OverwrittenSampleCount = protocol.OverwrittenSampleCount,
                OldestRetainedSampleAgeSeconds = null,
                NewestRetainedSampleAgeSeconds = null,
            }))
            .OrderBy(item => item.Protocol, StringComparer.Ordinal)
            .ToList();

        return new OperateSloView
        {
            Configured = false,
            Reason = options.HasAvailabilityTarget
                ? "A target is configured, but platform availability requires a distributed, all-request, in-band-aware query; the replica-local retained tail is diagnostic only."
                : "Platform availability requires a distributed, all-request, in-band-aware query; no qualifying platform SLO source is configured.",
            Availability = null,
            NodeLocalRetainedTail = new OperateNodeLocalRetainedTailView
            {
                Scope = "replica-local",
                IsPlatformSli = false,
                ConfiguredTarget = options.Availability.Target,
                ConfiguredWindowSeconds = options.Availability.RollingWindowSeconds,
                RetentionWindowSeconds = snapshot.WindowSeconds,
                // Only protocols that still retain samples carry a meaningful age. A protocol that
                // idled past the window reports its lifetime counters with a zero age, and must not
                // drag the aggregate "newest" age to zero as though a sample had just arrived.
                OldestRetainedSampleAgeSeconds = retaining.Count == 0 ? null : retaining.Max(item => item.OldestRetainedSampleAgeSeconds),
                NewestRetainedSampleAgeSeconds = retaining.Count == 0 ? null : retaining.Min(item => item.NewestRetainedSampleAgeSeconds),

                RetainedRequestCount = requestCount,
                RetainedHttpServerErrorCount = errorCount,
                RetainedHttpSuccessRatio = requestCount == 0 ? null : 1.0 - ((double)errorCount / requestCount),
                TotalRecordedSinceReset = totalRecorded,
                OverwrittenSampleCount = overwritten,
                IncludesInBandErrors = false,
                ResetBehavior = "process-start-or-telemetry-reconfigure",
                Source = DiagnosticSource,
                Protocols = protocols,
            },
        };
    }
}
