// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ServiceDefaults;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// Pure evaluation of the minimal v1 availability SLO from the telemetry the server already
/// aggregates in-process. No metrics database is involved: availability is computed from the
/// GIS-protocol-partitioned serving-latency rolling window (request and server-error counts).
/// </summary>
/// <remarks>
/// <para>
/// <b>What v1 actually evaluates:</b> observed availability = 1 - (server errors / requests) over the
/// aggregator's window, where a "server error" is an HTTP response with status &gt;= 500
/// (<c>ServingLatencyAggregator.ServerErrorFloor</c>). The error budget is <c>1 - target</c>; the burn
/// rate is the observed error fraction divided by that budget; remaining budget is
/// <c>1 - burnRate</c> clamped to [0, 1].
/// </para>
/// <para>
/// <b>Honest limitation / where a richer signal would plug in:</b> the in-process rolling window
/// counts 5xx serving errors only. The GIS-aware <em>in-band</em> GeoServices error signal (error
/// envelopes delivered with a 2xx status, emitted as the cumulative <c>honua_geoservices_error_total</c>
/// / <c>honua_request_error_total{in_band}</c> counters) is not yet windowed, so it does not currently
/// contribute to this availability figure. Adding a windowed aggregator for those counters and folding
/// its error count into <see cref="Evaluate"/> is the single, well-scoped extension point; the
/// contract shape here does not change when that lands.
/// </para>
/// </remarks>
internal static class OperateSloEvaluator
{
    internal const string EvaluationSource = "serving-latency-window(http-5xx)";

    /// <summary>
    /// Evaluates the availability SLO against a serving-latency snapshot.
    /// </summary>
    /// <param name="options">The SLO configuration.</param>
    /// <param name="snapshot">The in-process serving-latency snapshot.</param>
    /// <returns>The SLO view: configured with an evaluation, or the explicit not-configured state.</returns>
    public static OperateSloView Evaluate(OperateSloOptions options, ServingLatencySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!options.HasAvailabilityTarget)
        {
            return new OperateSloView
            {
                Configured = false,
                Reason = "No availability SLO target is configured. Set Slo:Availability:Target to a "
                    + "fraction in (0, 1) (for example 0.995) to evaluate an error budget from the "
                    + "in-process serving-latency window.",
                Availability = null,
            };
        }

        var target = options.Availability.Target!.Value;

        long requestCount = 0;
        long errorCount = 0;
        foreach (var protocol in snapshot.Protocols)
        {
            requestCount += protocol.RequestCount;
            errorCount += protocol.ErrorCount;
        }

        double? observed = null;
        double? burnRate = null;
        double? budgetRemaining = null;

        if (requestCount > 0)
        {
            var errorFraction = (double)errorCount / requestCount;
            observed = 1.0 - errorFraction;

            var errorBudget = 1.0 - target; // allowed error fraction
            if (errorBudget <= 0)
            {
                // A target of 1.0 admits no error budget; any error is an infinite burn. Guarded here
                // even though HasAvailabilityTarget excludes target >= 1.
                burnRate = errorFraction > 0 ? double.PositiveInfinity : 0.0;
                budgetRemaining = errorFraction > 0 ? 0.0 : 1.0;
            }
            else
            {
                burnRate = errorFraction / errorBudget;
                budgetRemaining = Math.Clamp(1.0 - burnRate.Value, 0.0, 1.0);
            }
        }

        return new OperateSloView
        {
            Configured = true,
            Reason = null,
            Availability = new OperateSloAvailabilityView
            {
                Target = target,
                WindowSeconds = snapshot.WindowSeconds,
                RequestCount = requestCount,
                ErrorCount = errorCount,
                Observed = observed,
                BurnRate = burnRate,
                ErrorBudgetRemaining = budgetRemaining,
                EvaluationSource = EvaluationSource,
            },
        };
    }

    /// <summary>
    /// Determines whether a configured SLO has exhausted its error budget over the window (a verdict
    /// degradation signal). Only true when there is traffic to evaluate and the remaining budget is
    /// at or below zero.
    /// </summary>
    /// <param name="slo">The evaluated SLO view.</param>
    /// <returns><see langword="true"/> when the error budget is exhausted.</returns>
    public static bool IsErrorBudgetExhausted(OperateSloView slo)
        => slo.Configured
           && slo.Availability is { ErrorBudgetRemaining: <= 0.0 };
}
