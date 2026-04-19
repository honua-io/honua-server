// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;

namespace Honua.Server.Features.Grounding;

/// <summary>
/// Builds a <see cref="DraftIntent"/> from engine output. Separated from the
/// service so unit tests can verify envelope shape for every workflow family
/// without spinning up the full ranking pipeline.
/// </summary>
internal static class IntentDrafter
{
    /// <summary>
    /// Produces the draft intent for the classified workflow family. For
    /// families whose domain records have not shipped yet (BuildApp,
    /// AutomateDeploy), emits a schema-stable envelope with only the common
    /// fields populated — the clarification reason list on the caller side
    /// carries the <see cref="ClarificationReasonCode.PolicyBoundary"/>
    /// pointer.
    /// </summary>
    public static DraftIntent Draft(
        GroundingRequest request,
        string intentId,
        WorkflowFamilyClassification classification,
        CandidateRanking candidates,
        IReadOnlyList<string> clarificationQuestionIds,
        IReadOnlyList<string> assumptions)
    {
        var requestedOutputs = InferRequestedOutputs(classification.Value, request);
        var provenance = BuildProvenance(candidates, clarificationQuestionIds, assumptions);

        return classification.Value switch
        {
            WorkflowFamily.Analyze => new DraftIntent
            {
                IntentId = intentId,
                Goal = request.Goal,
                WorkflowFamily = classification.Value,
                RequestedOutputs = requestedOutputs,
                AssumptionPolicy = request.AssumptionPolicy,
                Analysis = BuildAnalysisIntent(request, intentId, requestedOutputs),
                Provenance = provenance
            },
            WorkflowFamily.PublishData => new DraftIntent
            {
                IntentId = intentId,
                Goal = request.Goal,
                WorkflowFamily = classification.Value,
                RequestedOutputs = requestedOutputs,
                AssumptionPolicy = request.AssumptionPolicy,
                Publishing = BuildPublishIntent(request, intentId, candidates),
                Provenance = provenance
            },
            _ => new DraftIntent
            {
                IntentId = intentId,
                Goal = request.Goal,
                WorkflowFamily = classification.Value,
                RequestedOutputs = requestedOutputs,
                AssumptionPolicy = request.AssumptionPolicy,
                Provenance = provenance
            }
        };
    }

    private static AnalysisIntent BuildAnalysisIntent(
        GroundingRequest request,
        string intentId,
        IReadOnlyList<ArtifactKind> requestedOutputs) => new()
        {
            IntentId = intentId,
            Goal = request.Goal,
            Mode = request.WorkflowFamilyHint.HasValue ? "analysis" : null,
            RequestedOutputs = requestedOutputs,
            Constraints = request.Constraints,
            Inputs = request.ExplicitInputs,
            AssumptionPolicy = request.AssumptionPolicy
        };

    private static PublishIntent? BuildPublishIntent(
        GroundingRequest request,
        string intentId,
        CandidateRanking candidates)
    {
        // Draft a publish intent only when the caller has pinned a source
        // via ExplicitInputs or when a high-confidence dataset leads the
        // ranking. Otherwise the clarification envelope asks the source
        // question and the caller comes back with a resolved input.
        string? sourceId = null;
        if (request.ExplicitInputs.Count > 0)
        {
            sourceId = request.ExplicitInputs[0];
        }
        else if (candidates.Datasets.Count > 0
                 && candidates.Datasets[0].ConfidenceBand == ConfidenceBand.High)
        {
            sourceId = candidates.Datasets[0].Id;
        }

        if (sourceId is null)
        {
            return null;
        }

        return PublishIntent.CreateDraft(
            intentId: intentId,
            sourceKind: PublishSourceKind.FeatureLayer,
            sourceId: sourceId,
            targetKind: PublishTargetKind.FeatureService);
    }

    private static IReadOnlyList<ArtifactKind> InferRequestedOutputs(
        WorkflowFamily family,
        GroundingRequest request)
    {
        var tokens = GroundingTokenizer.TokenizeToSet(request.Goal);
        if (tokens.Count == 0)
        {
            return family == WorkflowFamily.BuildApp
                ? [ArtifactKind.AppBundle]
                : [ArtifactKind.FeatureLayer];
        }

        if (tokens.Contains("map"))
        {
            return [ArtifactKind.Map];
        }

        if (tokens.Contains("app") || tokens.Contains("dashboard") || tokens.Contains("viewer"))
        {
            return [ArtifactKind.AppBundle];
        }

        if (tokens.Contains("report"))
        {
            return [ArtifactKind.Report];
        }

        if (tokens.Contains("raster") || tokens.Contains("tile"))
        {
            return [ArtifactKind.Raster];
        }

        if (tokens.Contains("count") || tokens.Contains("area") || tokens.Contains("length"))
        {
            return [ArtifactKind.Scalar];
        }

        if (tokens.Contains("tabl") || tokens.Contains("csv") || tokens.Contains("summary"))
        {
            return [ArtifactKind.Table];
        }

        return family switch
        {
            WorkflowFamily.BuildApp => [ArtifactKind.AppBundle],
            WorkflowFamily.PublishData => [ArtifactKind.FeatureLayer],
            _ => [ArtifactKind.FeatureLayer]
        };
    }

    private static ProvenanceRecord BuildProvenance(
        CandidateRanking candidates,
        IReadOnlyList<string> clarificationQuestionIds,
        IReadOnlyList<string> assumptions)
    {
        var sources = new List<ProvenanceSource>();
        foreach (var dataset in candidates.Datasets)
        {
            sources.Add(new ProvenanceSource
            {
                SourceId = dataset.Id,
                Description = dataset.DisplayName
            });
        }

        var processIds = new List<string>(candidates.Processes.Count);
        foreach (var process in candidates.Processes)
        {
            processIds.Add(process.Id);
        }

        return new ProvenanceRecord
        {
            Sources = sources,
            ProcessDefinitions = processIds,
            Assumptions = assumptions,
            ClarificationsAsked = clarificationQuestionIds
        };
    }
}
