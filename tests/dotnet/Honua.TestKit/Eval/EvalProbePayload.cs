// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.TestKit.Eval;

/// <summary>
/// Wire shape of the body posted to the OGC API Processes execution endpoint
/// by the protocol-parity probe. Mirrors the OGC canonical inputs envelope:
/// <c>{ "inputs": { "plan": &lt;AnalysisPlan&gt; } }</c>.
/// </summary>
public sealed record EvalProbePayload
{
    /// <summary>Inputs envelope carrying the precompiled plan.</summary>
    [JsonPropertyName("inputs")]
    public EvalProbePayloadInputs Inputs { get; init; } = new();
}

/// <summary>Inputs object for <see cref="EvalProbePayload"/>.</summary>
public sealed record EvalProbePayloadInputs
{
    /// <summary>The precompiled plan to submit for execution.</summary>
    [JsonPropertyName("plan")]
    public EvalPlanSpec Plan { get; init; } = new();
}
