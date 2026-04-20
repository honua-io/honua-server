// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.TestKit.Eval;

/// <summary>
/// Declarative end-to-end eval scenario describing the canonical operator workflow
/// from intent capture through terminal result validation. Scenarios are authored as
/// JSON under <c>tests/Eval/scenarios/</c> and consumed by <see cref="EvalRunner"/>.
/// </summary>
public sealed record EvalScenario
{
    /// <summary>Stable scenario identifier used in reports and CI filters.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable scenario name for reports.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Workflow mode: analysis | publish | package | deploy.</summary>
    [JsonPropertyName("mode")]
    public EvalScenarioMode Mode { get; init; } = EvalScenarioMode.Analysis;

    /// <summary>
    /// Seed profile applied to the class-scoped eval schema (e.g. <c>core</c>,
    /// <c>ogc</c>). All scenarios in one harness run must agree on this profile.
    /// </summary>
    [JsonPropertyName("fixtureProfile")]
    public string FixtureProfile { get; init; } = "core";

    /// <summary>Analyst intent captured at the start of the workflow.</summary>
    [JsonPropertyName("intent")]
    public EvalIntentSpec Intent { get; init; } = new();

    /// <summary>Precompiled plan supplied by the scenario (Phase 1 compile seam).</summary>
    [JsonPropertyName("precompiledPlan")]
    public EvalPlanSpec PrecompiledPlan { get; init; } = new();

    /// <summary>Expected outcome envelope asserted by the runner.</summary>
    [JsonPropertyName("expectedOutcome")]
    public EvalExpectedOutcome ExpectedOutcome { get; init; } = new();
}

/// <summary>Workflow mode hint for a scenario.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvalScenarioMode>))]
public enum EvalScenarioMode
{
    /// <summary>Analysis-only workflow.</summary>
    Analysis,

    /// <summary>Publish lifecycle workflow (surface wiring in #730).</summary>
    Publish,

    /// <summary>Map package composition workflow (surface wiring in #730).</summary>
    Package,

    /// <summary>Deployment promotion workflow (surface wiring in #732).</summary>
    Deploy
}

/// <summary>Serialized shape of <see cref="AnalysisIntent"/> for a scenario.</summary>
public sealed record EvalIntentSpec
{
    /// <summary>Intent identifier.</summary>
    [JsonPropertyName("intentId")]
    public string IntentId { get; init; } = string.Empty;

    /// <summary>Natural-language goal.</summary>
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    /// <summary>Optional mode hint stored on the domain intent.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>Requested output artifact kinds.</summary>
    [JsonPropertyName("requestedOutputs")]
    public IReadOnlyList<ArtifactKind> RequestedOutputs { get; init; } = [];

    /// <summary>Spatial, temporal, and unit constraints.</summary>
    [JsonPropertyName("constraints")]
    public EvalIntentConstraints? Constraints { get; init; }

    /// <summary>Dataset or layer references.</summary>
    [JsonPropertyName("inputs")]
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>Strategy for handling assumptions during compilation.</summary>
    [JsonPropertyName("assumptionPolicy")]
    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;
}

/// <summary>Serialized shape of <see cref="IntentConstraints"/> for a scenario.</summary>
public sealed record EvalIntentConstraints
{
    /// <summary>WKT polygon, bounding box, or named area-of-interest.</summary>
    [JsonPropertyName("areaOfInterest")]
    public string? AreaOfInterest { get; init; }

    /// <summary>EPSG SRID for the area-of-interest.</summary>
    [JsonPropertyName("spatialReferenceId")]
    public int? SpatialReferenceId { get; init; }

    /// <summary>Time-window start (ISO 8601).</summary>
    [JsonPropertyName("timeWindowStart")]
    public DateTimeOffset? TimeWindowStart { get; init; }

    /// <summary>Time-window end (ISO 8601).</summary>
    [JsonPropertyName("timeWindowEnd")]
    public DateTimeOffset? TimeWindowEnd { get; init; }

    /// <summary>Preferred unit system (e.g. "meters").</summary>
    [JsonPropertyName("units")]
    public string? Units { get; init; }
}

/// <summary>Serialized shape of <see cref="AnalysisPlan"/> for a scenario.</summary>
public sealed record EvalPlanSpec
{
    /// <summary>Plan identifier.</summary>
    [JsonPropertyName("planId")]
    public string PlanId { get; init; } = string.Empty;

    /// <summary>Back-reference to the originating intent identifier.</summary>
    [JsonPropertyName("intentId")]
    public string IntentId { get; init; } = string.Empty;

    /// <summary>Ordered plan steps forming the execution DAG.</summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<EvalPlanStepSpec> Steps { get; init; } = [];

    /// <summary>Artifact kinds the plan promises to produce.</summary>
    [JsonPropertyName("outputs")]
    public IReadOnlyList<ArtifactKind> Outputs { get; init; } = [];
}

/// <summary>Serialized shape of <see cref="AnalysisPlanStep"/> for a scenario.</summary>
public sealed record EvalPlanStepSpec
{
    /// <summary>Step identifier.</summary>
    [JsonPropertyName("stepId")]
    public string StepId { get; init; } = string.Empty;

    /// <summary>Step category.</summary>
    [JsonPropertyName("kind")]
    public AnalysisPlanStepKind Kind { get; init; }

    /// <summary>Process identifier for <see cref="AnalysisPlanStepKind.Geoprocess"/> steps.</summary>
    [JsonPropertyName("processId")]
    public string? ProcessId { get; init; }

    /// <summary>Step-specific inputs, mirroring the proto map&lt;string,string&gt; contract.</summary>
    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>Upstream step identifiers.</summary>
    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>Expected-outcome envelope asserted by the runner.</summary>
public sealed record EvalExpectedOutcome
{
    /// <summary>Expected <see cref="PlanValidationResult.IsExecutable"/> value.</summary>
    [JsonPropertyName("isExecutable")]
    public bool IsExecutable { get; init; } = true;

    /// <summary>Expected <see cref="PlanValidationResult.RequiresApproval"/> value.</summary>
    [JsonPropertyName("requiresApproval")]
    public bool RequiresApproval { get; init; }

    /// <summary>Expected <see cref="DryRunResult.EstimatedArtifacts"/> set.</summary>
    [JsonPropertyName("estimatedArtifactKinds")]
    public IReadOnlyList<ArtifactKind> EstimatedArtifactKinds { get; init; } = [];

    /// <summary>Expected terminal workflow status once execution completes.</summary>
    [JsonPropertyName("terminalWorkflowStatus")]
    public GeoprocessingWorkflowStatus? TerminalWorkflowStatus { get; init; }

    /// <summary>Whether the scenario expects a <see cref="AnalysisResultPackage.MapPackageId"/>.</summary>
    [JsonPropertyName("expectsMapPackage")]
    public bool ExpectsMapPackage { get; init; }

    /// <summary>Whether the scenario expects a <see cref="AnalysisResultPackage.AppPackageId"/>.</summary>
    [JsonPropertyName("expectsAppPackage")]
    public bool ExpectsAppPackage { get; init; }
}
