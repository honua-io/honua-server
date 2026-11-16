// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.TestKit.Eval;

public sealed record EvalProbePayload
{
    [JsonPropertyName("inputs")]
    public EvalProbePayloadInputs Inputs { get; init; } = new();
}

public sealed record EvalProbePayloadInputs
{
    [JsonPropertyName("plan")]
    public EvalPlanSpec Plan { get; init; } = new();
}
