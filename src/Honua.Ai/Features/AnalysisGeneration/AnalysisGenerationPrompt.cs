// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Ai.AnalysisGeneration;

/// <summary>
/// Builds the grounded system/user prompts for analysis generation. Mirrors
/// <c>FormGenerationPrompt</c>/<c>WorkflowGenerationPrompt</c>: a lean, deterministic prompt that
/// enumerates the supported analysis-method vocabulary (the process catalog — every <c>processId</c>
/// with its category, summary, and parameter specs) and the analysis-package contract so a local
/// model proposes only plans whose steps reference real methods with their declared parameters.
/// </summary>
internal static class AnalysisGenerationPrompt
{
    public static string BuildSystem(AnalysisGenerationProviderRequest request, IProcessCatalog catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Honua's GIS analysis author. You convert a natural-language request into a validated analysis package that the geoprocessing execution engine can run.");
        sb.AppendLine();
        sb.AppendLine("An analysis package has:");
        sb.AppendLine("- intent: { intentId (a short handle you invent), goal (restate the user's goal), mode (\"analysis\"), inputs (named dataset/layer references the user gave), requestedOutputs (artifact kinds) }.");
        sb.AppendLine("- plan: { planId (a short handle you invent), intentId (match the intent), steps[], outputs (artifact kinds the plan promises) }.");
        sb.AppendLine("- requestedArtifacts: the artifact kinds the package should produce.");
        sb.AppendLine();
        sb.AppendLine("Each plan step has: stepId (unique short handle), kind, processId (for Geoprocess steps), inputs (a string->string map of parameter name to value), dependsOn (stepIds that must finish first).");
        sb.AppendLine("Step kinds: QueryFeatures (read features from a source), Geoprocess (run a catalog method), Aggregate, RenderMap, Export.");
        sb.AppendLine("Artifact kinds: Scalar, FeatureLayer, Table, Raster, File, Report, Map, AppBundle.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- The core of the analysis is one or more Geoprocess steps. Each Geoprocess step MUST set processId to a method id from the catalog below, and MUST supply every required parameter for that method in its inputs. Never invent a processId or a parameter name.");
        sb.AppendLine("- Choose the method whose purpose matches the request (e.g. buffer, spatial join, clustering, density/hotspot binning, dissolve, slope/hillshade). Set parameter values from the request; for an input layer the user named but whose numeric layerId is unknown, use \"0\" and note it in the rationale — the operator binds the real layer at run/publish time.");
        sb.AppendLine("- Respect each parameter's allowed values and value type (e.g. an algorithm enum, a positive distance). Conditionally-required parameters (e.g. eps+minPoints when algorithm=dbscan, k when algorithm=kmeans, distance when predicate=dwithin) must be present when their condition holds.");
        sb.AppendLine("- Keep plans minimal: prefer a single Geoprocess step unless the request clearly needs a pipeline. Order multi-step plans with dependsOn.");
        sb.AppendLine("- Set plan.outputs and requestedArtifacts to the method's natural output (e.g. a FeatureLayer for buffer/spatial-join/cluster, a Scalar for area/length, a Raster for slope/hillshade).");
        sb.AppendLine("- If the request is ambiguous (which method, which input layer, which parameter value), return status \"needs-clarification\" with clarifications instead of guessing.");
        sb.AppendLine("- If the request is not about building a GIS analysis, return status \"refused\" with a short rationale.");
        sb.AppendLine("- Otherwise return status \"generated\" with the analysis and a one-paragraph rationale.");
        sb.AppendLine();

        if (request.RepairFailures is { Count: > 0 })
        {
            sb.AppendLine("Your previous analysis failed server validation. Fix these failures and return a corrected analysis:");
            foreach (var failure in request.RepairFailures)
            {
                sb.Append("- [").Append(failure.Code).Append("] ").Append(failure.Message);
                if (!string.IsNullOrWhiteSpace(failure.FieldPath))
                {
                    sb.Append(" (at ").Append(failure.FieldPath).Append(')');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        if (request.AvailableSources is { Count: > 0 })
        {
            sb.AppendLine("REAL published layers available in this workspace. When the analysis reads one of these as an");
            sb.AppendLine("input, you MUST set that input's layerId to its EXACT numeric id below (not 0) and its serviceId to");
            sb.AppendLine("the EXACT service id shown, and pick any field names ONLY from its field list:");
            foreach (var source in request.AvailableSources)
            {
                sb.Append("- layerId=").Append(source.LayerId).Append(" serviceId=\"").Append(source.ServiceId).Append('"');
                if (!string.IsNullOrWhiteSpace(source.Name))
                {
                    sb.Append(" name=\"").Append(source.Name).Append('"');
                }

                if (source.Fields is { Length: > 0 } fields)
                {
                    sb.Append(" fields=[").Append(string.Join(", ", fields)).Append(']');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.AppendLine("Available analysis methods (processId — Title [category]: description; parameters):");
        AppendMethodCatalog(sb, catalog);
        return sb.ToString();
    }

    public static string BuildUser(AnalysisGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(request.Prompt);

        if (request.Answers is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Answers to prior clarifications:");
            foreach (var answer in request.Answers)
            {
                sb.Append("- ").Append(answer.QuestionId).Append(" = ").AppendLine(answer.OptionId);
            }
        }

        if (request.CurrentAnalysis?.Plan?.Steps is { Count: > 0 } steps)
        {
            sb.AppendLine();
            sb.Append("Refine the existing analysis, which currently has ")
              .Append(steps.Count)
              .AppendLine(" step(s). Preserve existing steps unless the request asks to change them.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Enumerates the live process catalog as the grounded method vocabulary. One line per method with
    /// its required/optional parameters (and allowed-value enums where declared) so the model can only
    /// pick a real method and supply its real parameters — the analysis-domain counterpart to grounding
    /// workflow generation in the node registry.
    /// </summary>
    private static void AppendMethodCatalog(StringBuilder sb, IProcessCatalog catalog)
    {
        foreach (var process in catalog.ListProcesses().OrderBy(p => p.ProcessId, StringComparer.Ordinal))
        {
            sb.Append("- ").Append(process.ProcessId)
              .Append(" — ").Append(process.Title)
              .Append(" [").Append(process.Category).Append("]: ")
              .Append(process.Description);

            if (process.Parameters.Count > 0)
            {
                sb.Append(" Parameters: ");
                sb.Append(string.Join("; ", process.Parameters.Select(DescribeParameter)));
            }

            sb.AppendLine();
        }
    }

    private static string DescribeParameter(ProcessParameterSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append(spec.Name).Append(" (").Append(spec.ValueType);
        if (spec.Required)
        {
            sb.Append(", required");
        }

        if (spec.AllowedValues is { Count: > 0 })
        {
            sb.Append(", one of: ").Append(string.Join('/', spec.AllowedValues));
        }

        if (!string.IsNullOrWhiteSpace(spec.DefaultValue))
        {
            sb.Append(", default ").Append(spec.DefaultValue);
        }

        sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>
/// Provider-facing request for a single analysis-generation turn: the prompt, prior turns, answered
/// clarifications, the current analysis (refine), and validator failures fed back during repair.
/// </summary>
internal sealed record AnalysisGenerationProviderRequest
{
    public required string Prompt { get; init; }
    public string? ModelOverride { get; init; }
    public IReadOnlyList<AnalysisGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<AnalysisGenerationAnswer> Answers { get; init; } = [];
    public AnalysisPackageContent? CurrentAnalysis { get; init; }
    public IReadOnlyList<GeoprocessingValidationFailure> RepairFailures { get; init; } = [];
    public IReadOnlyList<AnalysisGenerationSource> AvailableSources { get; init; } = [];
}
