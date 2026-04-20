// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Geoprocessing;

namespace Honua.Server.Features.Grounding;

/// <summary>
/// Applies the ADR-0027 material-ambiguity rule set to a grounding pass.
/// Centralised here so the rule set is a single reviewable function and the
/// conformance harness can pin the exact order of findings.
/// </summary>
internal static class MaterialAmbiguityEvaluator
{
    public static IReadOnlyList<MaterialAmbiguityFinding> Evaluate(
        GroundingRequest request,
        WorkflowFamilyClassification classification,
        CandidateRanking candidates,
        IReadOnlyList<ProcessParameterSpec> requiredParameterGaps,
        GroundingOptions options)
    {
        var findings = new List<MaterialAmbiguityFinding>(capacity: 3);

        // 1. LowConfidence comes first — if the workflow family itself is
        // unclear, the caller must resolve that before anything else.
        if (classification.Confidence < options.WorkflowFamilyFloor)
        {
            findings.Add(new MaterialAmbiguityFinding
            {
                ReasonCode = ClarificationReasonCode.LowConfidence,
                QuestionId = "workflow_family",
                QuestionKind = ClarificationQuestionKind.SingleSelect,
                Prompt = "Which workflow matches your goal?",
                Options =
                [
                    new ClarificationOption { Id = nameof(WorkflowFamily.Analyze), Label = "Analyze data" },
                    new ClarificationOption { Id = nameof(WorkflowFamily.PublishData), Label = "Publish data as a service" },
                    new ClarificationOption { Id = nameof(WorkflowFamily.BuildApp), Label = "Build an app or dashboard" },
                    new ClarificationOption { Id = nameof(WorkflowFamily.AutomateDeploy), Label = "Automate a deployment" }
                ]
            });
        }

        // 2. MissingRequiredInput — any required process parameter without an
        // inferable default.
        foreach (var parameter in requiredParameterGaps)
        {
            findings.Add(new MaterialAmbiguityFinding
            {
                ReasonCode = ClarificationReasonCode.MissingRequiredInput,
                QuestionId = $"param.{parameter.Name}",
                QuestionKind = ClarificationQuestionKind.FreeText,
                Prompt = $"Provide a value for required parameter '{parameter.DisplayName}': {parameter.Description}"
            });
        }

        // 3. AmbiguousDataset / AmbiguousProcess — multiple high-confidence
        // candidates within the material spread.
        if (IsAmbiguous(candidates.Datasets, options, out var datasetOptions))
        {
            findings.Add(new MaterialAmbiguityFinding
            {
                ReasonCode = ClarificationReasonCode.AmbiguousDataset,
                QuestionId = "dataset.selection",
                QuestionKind = ClarificationQuestionKind.SingleSelect,
                Prompt = "Which dataset do you want to use?",
                Options = datasetOptions
            });
        }

        if (IsAmbiguous(candidates.Processes, options, out var processOptions))
        {
            findings.Add(new MaterialAmbiguityFinding
            {
                ReasonCode = ClarificationReasonCode.AmbiguousProcess,
                QuestionId = "process.selection",
                QuestionKind = ClarificationQuestionKind.SingleSelect,
                Prompt = "Which operation do you want to run?",
                Options = processOptions
            });
        }

        // 4. DestructiveAction — top process candidate is flagged destructive.
        if (candidates.Processes.Count > 0
            && ProcessDestructiveClassifier.IsDestructive(candidates.Processes[0].Id))
        {
            findings.Add(new MaterialAmbiguityFinding
            {
                ReasonCode = ClarificationReasonCode.DestructiveAction,
                QuestionId = "destructive.confirm",
                QuestionKind = ClarificationQuestionKind.Confirmation,
                Prompt = $"'{candidates.Processes[0].DisplayName}' mutates existing data. Confirm you want to proceed."
            });
        }

        // 5. PublishAction — workflow family is PublishData, or requested
        // outputs include a published surface.
        if (classification.Value == WorkflowFamily.PublishData)
        {
            // A publish draft needs a source id. If the caller has not pinned an
            // explicit input and the top dataset is not high-confidence, ask for
            // the source. Without this question, answering publish.target alone
            // on a follow-up turn can leave draftIntent.publishing == null with
            // no clarification to escape — a terminal dead state.
            if (!IsPublishSourceResolved(request, candidates))
            {
                // Service catalog entries cannot be published directly
                // (PublishSourceKind has no FeatureService value), so they
                // are filtered out of the publish.source options — offering
                // them would stage a selection the drafter would have to
                // reject or mislabel.
                var publishableDatasets = candidates.Datasets
                    .Where(c => c.DatasetSubtype != DatasetSubtype.Service)
                    .ToArray();

                var sourceOptions = publishableDatasets.Length > 0
                    ? publishableDatasets
                        .Select(c => new ClarificationOption
                        {
                            Id = c.Id,
                            Label = c.DisplayName ?? c.Id
                        })
                        .ToArray()
                    : null;

                findings.Add(new MaterialAmbiguityFinding
                {
                    ReasonCode = ClarificationReasonCode.MissingRequiredInput,
                    QuestionId = "publish.source",
                    QuestionKind = sourceOptions is null
                        ? ClarificationQuestionKind.FreeText
                        : ClarificationQuestionKind.SingleSelect,
                    Prompt = "Which dataset should be published?",
                    Options = sourceOptions
                });
            }

            findings.Add(new MaterialAmbiguityFinding
            {
                ReasonCode = ClarificationReasonCode.PublishAction,
                QuestionId = "publish.target",
                QuestionKind = ClarificationQuestionKind.SingleSelect,
                Prompt = "Where should the result be published?",
                Options =
                [
                    new ClarificationOption { Id = nameof(PublishTargetKind.FeatureService), Label = "Feature service" },
                    new ClarificationOption { Id = nameof(PublishTargetKind.TileService), Label = "Tile service" },
                    new ClarificationOption { Id = nameof(PublishTargetKind.MapService), Label = "Map service" },
                    new ClarificationOption { Id = nameof(PublishTargetKind.StaticExport), Label = "Static export" }
                ]
            });
        }

        // 6. PolicyBoundary — workflow family we do not yet draft end-to-end.
        if (classification.Value is WorkflowFamily.BuildApp or WorkflowFamily.AutomateDeploy)
        {
            findings.Add(new MaterialAmbiguityFinding
            {
                ReasonCode = ClarificationReasonCode.PolicyBoundary,
                QuestionId = "workflow_family.blocked",
                QuestionKind = ClarificationQuestionKind.Confirmation,
                Prompt = $"The '{classification.Value}' workflow family is staged as an envelope only in this release. Confirm to proceed with just the envelope."
            });
        }

        return findings;
    }

    /// <summary>
    /// Source is resolved when the caller has pinned at least one explicit
    /// input or the top-ranked dataset is a high-confidence layer-backed
    /// candidate. Mirrors the predicate <see cref="IntentDrafter"/> uses to
    /// decide whether to emit a <c>publishing</c> block — service catalog
    /// entries are excluded from auto-resolution so the evaluator and drafter
    /// stay aligned. A pinned <c>ExplicitInputs[0]</c> that matches a service
    /// candidate in the ranking is treated as unresolved so the drafter
    /// cannot silently mislabel a service id as <c>FeatureLayer</c>.
    /// </summary>
    private static bool IsPublishSourceResolved(GroundingRequest request, CandidateRanking candidates)
    {
        if (request.ExplicitInputs.Count > 0)
        {
            var pinnedId = request.ExplicitInputs[0];
            foreach (var candidate in candidates.Datasets)
            {
                if (string.Equals(candidate.Id, pinnedId, StringComparison.Ordinal)
                    && candidate.DatasetSubtype == DatasetSubtype.Service)
                {
                    return false;
                }
            }
            return true;
        }

        return candidates.Datasets.Count > 0
            && candidates.Datasets[0].ConfidenceBand == ConfidenceBand.High
            && candidates.Datasets[0].DatasetSubtype != DatasetSubtype.Service;
    }

    private static bool IsAmbiguous(
        IReadOnlyList<GroundingCandidate> candidates,
        GroundingOptions options,
        out IReadOnlyList<ClarificationOption> options_out)
    {
        if (candidates.Count < 2 || candidates[0].Score < options.HighConfidenceFloor)
        {
            options_out = [];
            return false;
        }

        var leadScore = candidates[0].Score;
        var options_list = new List<ClarificationOption>(capacity: candidates.Count);
        options_list.Add(new ClarificationOption
        {
            Id = candidates[0].Id,
            Label = candidates[0].DisplayName ?? candidates[0].Id
        });

        for (var i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].Score < options.HighConfidenceFloor)
            {
                break;
            }

            if (leadScore - candidates[i].Score > options.MaterialSpread)
            {
                break;
            }

            options_list.Add(new ClarificationOption
            {
                Id = candidates[i].Id,
                Label = candidates[i].DisplayName ?? candidates[i].Id
            });
        }

        if (options_list.Count < 2)
        {
            options_out = [];
            return false;
        }

        options_out = options_list;
        return true;
    }
}
