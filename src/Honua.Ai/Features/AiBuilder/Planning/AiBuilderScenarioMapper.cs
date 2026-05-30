// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.AiBuilder.Fixtures;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.AiBuilder.Planning;

/// <summary>
/// Pure mapping helpers from a parsed <see cref="AiBuilderScenario"/> JSON
/// envelope to the wire-format <see cref="McpPlanAnalysisOutput"/>. Kept in
/// one place so unit tests can pin the contract translation without spinning
/// up the full planner service.
/// </summary>
internal static class AiBuilderScenarioMapper
{
    public static McpPlanAnalysisOutput ToPlanAnalysisOutput(AiBuilderScenario scenario)
    {
        var output = new McpPlanAnalysisOutput
        {
            Status = MapStatus(scenario.Case),
            FixtureCase = scenario.Case,
            ContractVersion = scenario.ContractVersion
        };

        var draft = TryGet(scenario.Root, "draft");
        var plan = TryGet(scenario.Root, "plan");
        var apply = TryGet(scenario.Root, "apply");
        var clarification = TryGet(scenario.Root, "clarification");
        var capabilityState = TryGet(scenario.Root, "capabilityState");
        var error = TryGet(scenario.Root, "error");
        var topLevelWarnings = TryGet(scenario.Root, "warnings");

        if (plan.HasValue)
        {
            output.Plan = TryReadAnalysisPlan(plan.Value, scenario);
            output.Cache = TryReadCache(plan.Value);
            output.Estimate = TryReadEstimate(plan.Value);

            var planWarnings = TryGet(plan.Value, "warnings");
            if (planWarnings.HasValue)
            {
                output.Warnings = ReadWarnings(planWarnings.Value);
            }

            // Plan-level status overrides the scenario case when the fixture
            // declares "rejected" so callers see the deterministic mapping for
            // oversized-estimate and similar fail-at-plan cases.
            var planStatus = ReadStringProperty(plan.Value, "status");
            if (!string.IsNullOrWhiteSpace(planStatus))
            {
                output.Status = planStatus switch
                {
                    "rejected" => "rejected",
                    "planned" => output.Status, // keep scenario-derived status (planned/clarification_required)
                    _ => output.Status
                };
            }
        }

        if (draft.HasValue)
        {
            var specDraft = TryGet(draft.Value, "specDraft");
            if (specDraft.HasValue)
            {
                output.SpecDraft = TryReadSpecDraft(specDraft.Value);
            }
        }

        if (apply.HasValue)
        {
            output.AppPackage = TryReadAppPackage(apply.Value);
        }

        if (clarification.HasValue)
        {
            output.Clarification = ReadClarification(clarification.Value);
        }

        if (capabilityState.HasValue)
        {
            output.CapabilityState = ReadCapabilityState(capabilityState.Value);
        }

        if (topLevelWarnings.HasValue && output.Warnings.Count == 0)
        {
            output.Warnings = ReadWarnings(topLevelWarnings.Value);
        }

        if (error.HasValue)
        {
            output.Reason = ReadStringProperty(error.Value, "code")
                ?? ReadStringProperty(error.Value, "message");
            output.Status = "rejected";
        }
        else if (output.Status == "rejected")
        {
            output.Reason ??= "Planner rejected the intent; see warnings/estimate.";
        }

        return output;
    }

    private static string MapStatus(string scenarioCase) => scenarioCase switch
    {
        "success" => "planned",
        "cache-hit" => "planned",
        "apply-failure" => "planned",
        "ambiguity" => "clarification_required",
        "unsupported" => "unsupported",
        "auth-denied" => "rejected",
        "oversized" => "rejected",
        _ => "rejected"
    };

    private static McpAnalysisPlanOutput? TryReadAnalysisPlan(JsonElement plan, AiBuilderScenario scenario)
    {
        var dag = TryGet(plan, "dag");
        if (!dag.HasValue || dag.Value.ValueKind != JsonValueKind.Array || dag.Value.GetArrayLength() == 0)
        {
            return null;
        }

        var steps = new List<McpAnalysisPlanStepOutput>(dag.Value.GetArrayLength());
        foreach (var node in dag.Value.EnumerateArray())
        {
            var step = new McpAnalysisPlanStepOutput
            {
                StepId = ReadStringProperty(node, "nodeId") ?? string.Empty,
                Kind = NormalizeStepKind(ReadStringProperty(node, "kind")),
                ProcessId = ReadStringProperty(node, "processId"),
                DependsOn = ReadStringArray(node, "dependsOn"),
                Inputs = ReadStringMap(node, "inputs")
            };
            steps.Add(step);
        }

        return new McpAnalysisPlanOutput
        {
            PlanId = ReadStringProperty(plan, "planId") ?? scenario.Id,
            IntentId = scenario.Id,
            Steps = steps,
            Outputs = [],
            Warnings = ReadWarningCodes(TryGet(plan, "warnings"))
        };
    }

    /// <summary>
    /// Normalises the fixture's free-form <c>kind</c> strings into the
    /// canonical <see cref="Honua.Core.Features.Geoprocessing.Domain.AnalysisPlanStepKind"/>
    /// names that <c>Honua.Ai.Protocols.Mcp.Tools.McpToolHelpers.ToDomainPlan</c>
    /// accepts. Anything not recognised falls through to <c>Geoprocess</c> so
    /// the wire output never carries an invalid step kind — the operations
    /// dashboard's specialised kinds (service, report, package, …) are kept
    /// in the spec-draft output, not the analysis plan.
    /// </summary>
    private static string NormalizeStepKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Geoprocess";
        }

        return raw.ToLowerInvariant() switch
        {
            "queryfeatures" => "QueryFeatures",
            "geoprocess" => "Geoprocess",
            "aggregate" => "Aggregate",
            "rendermap" => "RenderMap",
            "export" or "package" or "artifact" => "Export",
            "service" or "report" or "filter" => "QueryFeatures",
            _ => "Geoprocess"
        };
    }

    private static McpSpecDraftOutput? TryReadSpecDraft(JsonElement specDraft)
    {
        var canonical = TryGet(specDraft, "canonicalSpecDocument");
        if (!canonical.HasValue)
        {
            return null;
        }

        var output = new McpSpecDraftOutput
        {
            ReviewStatus = ReadStringProperty(specDraft, "reviewStatus") ?? "reviewable",
            SpecKind = ReadStringProperty(specDraft, "specKind") ?? "CanonicalSpecDocument",
            GrammarVersion = ReadStringProperty(canonical.Value, "grammarVersion") ?? string.Empty,
            ProcessFamilyVersion = ReadStringProperty(canonical.Value, "processFamilyVersion") ?? string.Empty,
            SpecId = ReadStringProperty(canonical.Value, "specId")
        };

        var nodes = TryGet(canonical.Value, "nodes");
        if (nodes.HasValue && nodes.Value.ValueKind == JsonValueKind.Array)
        {
            var list = new List<McpSpecDraftNodeOutput>(nodes.Value.GetArrayLength());
            foreach (var node in nodes.Value.EnumerateArray())
            {
                list.Add(new McpSpecDraftNodeOutput
                {
                    Id = ReadStringProperty(node, "id") ?? string.Empty,
                    Kind = ReadStringProperty(node, "kind") ?? string.Empty,
                    Op = ReadStringProperty(node, "op"),
                    Inputs = ReadStringMap(node, "inputs"),
                    Parameters = ReadStringMap(node, "parameters")
                });
            }
            output.Nodes = list;
        }

        return output;
    }

    private static McpPlanAnalysisAppPackage? TryReadAppPackage(JsonElement apply)
    {
        var packages = TryGet(apply, "packages");
        if (!packages.HasValue || packages.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var appPackage = TryGet(packages.Value, "appPackage");
        if (!appPackage.HasValue || appPackage.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var output = new McpPlanAnalysisAppPackage
        {
            AppPackageId = ReadStringProperty(appPackage.Value, "appPackageId") ?? string.Empty,
            TemplateId = ReadStringProperty(appPackage.Value, "templateId"),
            TargetSdk = ReadStringProperty(appPackage.Value, "targetSdk"),
            Format = ReadStringProperty(appPackage.Value, "format"),
            MapPackageId = ReadStringProperty(appPackage.Value, "mapPackageId"),
            ResourceUri = ReadStringProperty(appPackage.Value, "resourceUri"),
            EntryPoint = ReadStringProperty(appPackage.Value, "entryPoint"),
            ManifestArtifactId = ReadStringProperty(appPackage.Value, "manifestArtifactId"),
            BundleArtifactId = ReadStringProperty(appPackage.Value, "bundleArtifactId"),
            GeneratedFiles = ReadStringArray(appPackage.Value, "generatedFiles"),
            DeliveryHints = ReadStringMap(appPackage.Value, "deliveryHints")
        };

        var manifestPreview = TryGet(appPackage.Value, "manifestPreview");
        if (manifestPreview.HasValue)
        {
            output.Widgets = ReadStringArray(manifestPreview.Value, "widgets");
            output.DataBindings = ReadStringMap(manifestPreview.Value, "dataBindings");
        }

        return output;
    }

    private static McpPlanAnalysisCache? TryReadCache(JsonElement plan)
    {
        var cache = TryGet(plan, "cache");
        if (!cache.HasValue || cache.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new McpPlanAnalysisCache
        {
            Key = ReadStringProperty(cache.Value, "key") ?? string.Empty,
            Hit = cache.Value.TryGetProperty("hit", out var hit)
                  && hit.ValueKind == JsonValueKind.True,
            Reproducibility = ReadStringProperty(cache.Value, "reproducibility")
        };
    }

    private static McpPlanAnalysisEstimate? TryReadEstimate(JsonElement plan)
    {
        var estimate = TryGet(plan, "estimate");
        if (!estimate.HasValue || estimate.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new McpPlanAnalysisEstimate
        {
            FeatureCount = estimate.Value.TryGetProperty("featureCount", out var fc) && fc.ValueKind == JsonValueKind.Number
                ? fc.GetInt64()
                : 0,
            Limit = estimate.Value.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number
                ? lim.GetInt64()
                : 0,
            Unit = ReadStringProperty(estimate.Value, "unit")
        };
    }

    private static McpPlanAnalysisClarification ReadClarification(JsonElement clarification)
    {
        var output = new McpPlanAnalysisClarification();
        var candidates = TryGet(clarification, "candidates");
        if (!candidates.HasValue || candidates.Value.ValueKind != JsonValueKind.Array)
        {
            return output;
        }

        var list = new List<McpPlanAnalysisClarificationCandidate>(candidates.Value.GetArrayLength());
        foreach (var candidate in candidates.Value.EnumerateArray())
        {
            list.Add(new McpPlanAnalysisClarificationCandidate
            {
                Kind = ReadStringProperty(candidate, "kind") ?? string.Empty,
                Field = ReadStringProperty(candidate, "field"),
                Reason = ReadStringProperty(candidate, "reason"),
                Options = ReadStringArray(candidate, "options")
            });
        }
        output.Candidates = list;
        return output;
    }

    private static McpPlanAnalysisCapabilityState ReadCapabilityState(JsonElement capabilityState) => new()
    {
        Name = ReadStringProperty(capabilityState, "name") ?? string.Empty,
        State = ReadStringProperty(capabilityState, "state") ?? string.Empty,
        Reason = ReadStringProperty(capabilityState, "reason")
    };

    private static List<McpPlanAnalysisWarning> ReadWarnings(JsonElement warningsArray)
    {
        if (warningsArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<McpPlanAnalysisWarning>(warningsArray.GetArrayLength());
        foreach (var warning in warningsArray.EnumerateArray())
        {
            list.Add(new McpPlanAnalysisWarning
            {
                Code = ReadStringProperty(warning, "code") ?? string.Empty,
                Message = ReadStringProperty(warning, "message")
            });
        }
        return list;
    }

    private static List<string> ReadWarningCodes(JsonElement? warningsArray)
    {
        if (!warningsArray.HasValue || warningsArray.Value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>(warningsArray.Value.GetArrayLength());
        foreach (var warning in warningsArray.Value.EnumerateArray())
        {
            var code = ReadStringProperty(warning, "code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                list.Add(code);
            }
        }
        return list;
    }

    private static List<string> ReadStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    list.Add(value);
                }
            }
        }
        return list;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement parent, string property)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!parent.TryGetProperty(property, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var item in obj.EnumerateObject())
        {
            map[item.Name] = item.Value.ValueKind switch
            {
                JsonValueKind.String => item.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => item.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => item.Value.GetRawText()
            };
        }
        return map;
    }

    private static JsonElement? TryGet(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Null ? null : value;
    }

    private static string? ReadStringProperty(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
