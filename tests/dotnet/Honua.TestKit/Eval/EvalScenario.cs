// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.TestKit.Eval;

// Declarative end-to-end eval scenario describing the canonical operator workflow
// from intent capture through terminal result validation. Scenarios are authored as
// JSON under tests/fixtures/eval/ and consumed by EvalRunner.
public sealed record EvalScenario
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public EvalScenarioMode Mode { get; init; } = EvalScenarioMode.Analysis;

    [JsonPropertyName("fixtureProfile")]
    public string FixtureProfile { get; init; } = "core";

    [JsonPropertyName("intent")]
    public EvalIntentSpec Intent { get; init; } = new();

    [JsonPropertyName("precompiledPlan")]
    public EvalPlanSpec PrecompiledPlan { get; init; } = new();

    [JsonPropertyName("expectedOutcome")]
    public EvalExpectedOutcome ExpectedOutcome { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<EvalScenarioMode>))]
public enum EvalScenarioMode
{
    Analysis,

    Publish,

    Package,

    Deploy
}

public sealed record EvalIntentSpec
{
    [JsonPropertyName("intentId")]
    public string IntentId { get; init; } = string.Empty;

    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("requestedOutputs")]
    public IReadOnlyList<ArtifactKind> RequestedOutputs { get; init; } = [];

    [JsonPropertyName("constraints")]
    public EvalIntentConstraints? Constraints { get; init; }

    [JsonPropertyName("inputs")]
    public IReadOnlyList<string> Inputs { get; init; } = [];

    [JsonPropertyName("assumptionPolicy")]
    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;
}

public sealed record EvalIntentConstraints
{
    [JsonPropertyName("areaOfInterest")]
    public string? AreaOfInterest { get; init; }

    [JsonPropertyName("spatialReferenceId")]
    public int? SpatialReferenceId { get; init; }

    [JsonPropertyName("timeWindowStart")]
    public DateTimeOffset? TimeWindowStart { get; init; }

    [JsonPropertyName("timeWindowEnd")]
    public DateTimeOffset? TimeWindowEnd { get; init; }

    [JsonPropertyName("units")]
    public string? Units { get; init; }
}

public sealed record EvalPlanSpec
{
    [JsonPropertyName("planId")]
    public string PlanId { get; init; } = string.Empty;

    [JsonPropertyName("intentId")]
    public string IntentId { get; init; } = string.Empty;

    [JsonPropertyName("steps")]
    public IReadOnlyList<EvalPlanStepSpec> Steps { get; init; } = [];

    [JsonPropertyName("outputs")]
    public IReadOnlyList<ArtifactKind> Outputs { get; init; } = [];
}

public sealed record EvalPlanStepSpec
{
    [JsonPropertyName("stepId")]
    public string StepId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public AnalysisPlanStepKind Kind { get; init; }

    [JsonPropertyName("processId")]
    public string? ProcessId { get; init; }

    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

public sealed record EvalExpectedOutcome
{
    [JsonPropertyName("isExecutable")]
    public bool IsExecutable { get; init; } = true;

    [JsonPropertyName("directSubmitExecutable")]
    public bool? DirectSubmitExecutable { get; init; }

    [JsonIgnore]
    public bool ExpectsDirectSubmitExecution => DirectSubmitExecutable ?? IsExecutable;

    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; init; }

    [JsonPropertyName("estimatedArtifactKinds")]
    public IReadOnlyList<ArtifactKind> EstimatedArtifactKinds { get; init; } = [];

    [JsonPropertyName("terminalWorkflowStatus")]
    public GeoprocessingWorkflowStatus? TerminalWorkflowStatus { get; init; }

    [JsonPropertyName("expectsMapPackage")]
    public bool ExpectsMapPackage { get; init; }

    [JsonPropertyName("expectsAppPackage")]
    public bool ExpectsAppPackage { get; init; }
}
