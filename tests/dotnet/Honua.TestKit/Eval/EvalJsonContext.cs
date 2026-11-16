// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.TestKit.Eval;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(EvalScenario))]
[JsonSerializable(typeof(EvalIntentSpec))]
[JsonSerializable(typeof(EvalIntentConstraints))]
[JsonSerializable(typeof(EvalPlanSpec))]
[JsonSerializable(typeof(EvalPlanStepSpec))]
[JsonSerializable(typeof(EvalExpectedOutcome))]
[JsonSerializable(typeof(EvalReport))]
[JsonSerializable(typeof(EvalReportEnvironment))]
[JsonSerializable(typeof(EvalReportRollup))]
[JsonSerializable(typeof(EvalScenarioResult))]
[JsonSerializable(typeof(EvalStageOutcome))]
[JsonSerializable(typeof(EvalProtocolParityOutcome))]
[JsonSerializable(typeof(EvalProtocolProbe))]
[JsonSerializable(typeof(EvalProbePayload))]
[JsonSerializable(typeof(EvalProbePayloadInputs))]
[JsonSerializable(typeof(IReadOnlyList<EvalScenarioResult>))]
[JsonSerializable(typeof(IReadOnlyList<EvalStageOutcome>))]
[JsonSerializable(typeof(IReadOnlyList<EvalProtocolProbe>))]
public sealed partial class EvalJsonContext : JsonSerializerContext
{
}
