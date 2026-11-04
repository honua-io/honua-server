// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;
using OpenTelemetry.Trace;

namespace Honua.ServiceDefaults;

public sealed class AdaptiveOpenTelemetrySampler : Sampler
{
    private readonly IAdaptiveSampler _adaptiveSampler;
    private readonly TracingOptions _tracingOptions;

    public AdaptiveOpenTelemetrySampler(IAdaptiveSampler adaptiveSampler, TracingOptions tracingOptions)
    {
        _adaptiveSampler = adaptiveSampler ?? throw new ArgumentNullException(nameof(adaptiveSampler));
        _tracingOptions = tracingOptions ?? throw new ArgumentNullException(nameof(tracingOptions));
    }

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

    public new string Description =>
        $"AdaptiveOpenTelemetrySampler(currentRate={_adaptiveSampler.GetCurrentSamplingRate():P2})";
}
