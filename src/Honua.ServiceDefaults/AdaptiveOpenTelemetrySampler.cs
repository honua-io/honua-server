// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using OpenTelemetry.Trace;

namespace Honua.ServiceDefaults;

/// <summary>
/// OpenTelemetry sampler that integrates with the Honua adaptive sampling engine.
/// Bridges OpenTelemetry's sampling interface with intelligent adaptive sampling logic.
/// </summary>
public sealed class AdaptiveOpenTelemetrySampler : Sampler
{
    private readonly IAdaptiveSampler _adaptiveSampler;
    private readonly TracingOptions _tracingOptions;

    /// <summary>
    /// Initializes a new adaptive OpenTelemetry sampler.
    /// </summary>
    public AdaptiveOpenTelemetrySampler(IAdaptiveSampler adaptiveSampler, TracingOptions tracingOptions)
    {
        _adaptiveSampler = adaptiveSampler ?? throw new ArgumentNullException(nameof(adaptiveSampler));
        _tracingOptions = tracingOptions ?? throw new ArgumentNullException(nameof(tracingOptions));
    }

    /// <summary>
    /// Makes sampling decisions based on adaptive sampling logic.
    /// </summary>
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        // If tracing is disabled, never sample
        if (!_tracingOptions.Enabled)
        {
            return new SamplingResult(SamplingDecision.Drop);
        }

        // Extract operation name from the sampling parameters
        var operationName = samplingParameters.Name ?? "unknown";

        // Use adaptive sampling logic to make the decision
        var shouldSample = _adaptiveSampler.ShouldSample(operationName, samplingParameters.Kind);

        var decision = shouldSample ? SamplingDecision.RecordAndSample : SamplingDecision.Drop;

        return new SamplingResult(decision);
    }

    /// <summary>
    /// Returns a description of this sampler for debugging.
    /// </summary>
    public new string Description =>
        $"AdaptiveOpenTelemetrySampler(currentRate={_adaptiveSampler.GetCurrentSamplingRate():P2})";
}
