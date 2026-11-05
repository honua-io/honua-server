// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Models;

internal sealed class McpPlanAnalysisArgument
{
    [JsonPropertyName("intent")]
    public string? Intent { get; set; }

    [JsonPropertyName("context")]
    public JsonElement? Context { get; set; }
}

internal sealed class McpPlanAnalysisOutput
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    // An engine value of fixture is a canned demo, not a compilation of the caller's intent.
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;

    [JsonPropertyName("plan")]
    public McpAnalysisPlanOutput? Plan { get; set; }

    [JsonPropertyName("specDraft")]
    public McpSpecDraftOutput? SpecDraft { get; set; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<McpPlanAnalysisWarning> Warnings { get; set; } = [];

    [JsonPropertyName("cache")]
    public McpPlanAnalysisCache? Cache { get; set; }

    [JsonPropertyName("capabilityState")]
    public McpPlanAnalysisCapabilityState? CapabilityState { get; set; }

    [JsonPropertyName("clarification")]
    public McpPlanAnalysisClarification? Clarification { get; set; }

    [JsonPropertyName("estimate")]
    public McpPlanAnalysisEstimate? Estimate { get; set; }

    [JsonPropertyName("fixtureCase")]
    public string? FixtureCase { get; set; }

    [JsonPropertyName("contractVersion")]
    public string? ContractVersion { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("nextSteps")]
    public string? NextSteps { get; set; }

    [JsonPropertyName("appPackage")]
    public McpPlanAnalysisAppPackage? AppPackage { get; set; }

    [JsonPropertyName("context")]
    public JsonElement? Context { get; set; }
}

internal sealed class McpPlanAnalysisAppPackage
{
    [JsonPropertyName("appPackageId")]
    public string AppPackageId { get; set; } = string.Empty;

    [JsonPropertyName("templateId")]
    public string? TemplateId { get; set; }

    [JsonPropertyName("targetSdk")]
    public string? TargetSdk { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("mapPackageId")]
    public string? MapPackageId { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }

    [JsonPropertyName("entryPoint")]
    public string? EntryPoint { get; set; }

    [JsonPropertyName("manifestArtifactId")]
    public string? ManifestArtifactId { get; set; }

    [JsonPropertyName("bundleArtifactId")]
    public string? BundleArtifactId { get; set; }

    [JsonPropertyName("widgets")]
    public IReadOnlyList<string> Widgets { get; set; } = [];

    [JsonPropertyName("dataBindings")]
    public IReadOnlyDictionary<string, string> DataBindings { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("generatedFiles")]
    public IReadOnlyList<string> GeneratedFiles { get; set; } = [];

    [JsonPropertyName("deliveryHints")]
    public IReadOnlyDictionary<string, string> DeliveryHints { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed class McpAnalysisPlanOutput
{
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public IReadOnlyList<McpAnalysisPlanStepOutput> Steps { get; set; } = [];

    [JsonPropertyName("outputs")]
    public IReadOnlyList<string> Outputs { get; set; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

internal sealed class McpAnalysisPlanStepOutput
{
    [JsonPropertyName("stepId")]
    public string StepId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("processId")]
    public string? ProcessId { get; set; }

    [JsonPropertyName("dependsOn")]
    public IReadOnlyList<string> DependsOn { get; set; } = [];

    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, string> Inputs { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed class McpSpecDraftOutput
{
    [JsonPropertyName("reviewStatus")]
    public string ReviewStatus { get; set; } = "reviewable";

    [JsonPropertyName("specKind")]
    public string SpecKind { get; set; } = "CanonicalSpecDocument";

    [JsonPropertyName("grammarVersion")]
    public string GrammarVersion { get; set; } = string.Empty;

    [JsonPropertyName("processFamilyVersion")]
    public string ProcessFamilyVersion { get; set; } = string.Empty;

    [JsonPropertyName("specId")]
    public string? SpecId { get; set; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<McpSpecDraftNodeOutput> Nodes { get; set; } = [];
}

internal sealed class McpSpecDraftNodeOutput
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("op")]
    public string? Op { get; set; }

    [JsonPropertyName("inputs")]
    public IReadOnlyDictionary<string, string> Inputs { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string> Parameters { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed class McpPlanAnalysisWarning
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class McpPlanAnalysisCache
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("hit")]
    public bool Hit { get; set; }

    [JsonPropertyName("reproducibility")]
    public string? Reproducibility { get; set; }
}

internal sealed class McpPlanAnalysisCapabilityState
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal sealed class McpPlanAnalysisClarification
{
    [JsonPropertyName("candidates")]
    public IReadOnlyList<McpPlanAnalysisClarificationCandidate> Candidates { get; set; } = [];
}

internal sealed class McpPlanAnalysisClarificationCandidate
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("options")]
    public IReadOnlyList<string> Options { get; set; } = [];
}

internal sealed class McpPlanAnalysisEstimate
{
    [JsonPropertyName("featureCount")]
    public long FeatureCount { get; set; }

    [JsonPropertyName("limit")]
    public long Limit { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}
