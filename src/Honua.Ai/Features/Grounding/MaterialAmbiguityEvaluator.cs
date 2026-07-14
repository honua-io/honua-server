// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Geoprocessing;

namespace Honua.Ai.Grounding;

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
        => Evaluate(request, classification, candidates, requiredParameterGaps, options, out _);

    /// <summary>
    /// Evaluates material ambiguity and, as a side output, reports the bindings
    /// the service auto-resolved instead of clarifying. A binding is
    /// auto-resolved when exactly one candidate of a kind clears the
    /// high-confidence floor and dominates any runner-up by more than the
    /// material spread; in that case no <c>AmbiguousDataset</c>/
    /// <c>AmbiguousProcess</c> finding is emitted for that kind. Two or more
    /// viable candidates within the spread still surface the clarification, and
    /// zero candidates keep the existing missing-input path. The resolved
    /// bindings are auditable (id + score + runner-up margin) so the silent
    /// auto-bind stays explainable (honua-server#1949).
    /// </summary>
    public static IReadOnlyList<MaterialAmbiguityFinding> Evaluate(
        GroundingRequest request,
        WorkflowFamilyClassification classification,
        CandidateRanking candidates,
        IReadOnlyList<ProcessParameterSpec> requiredParameterGaps,
        GroundingOptions options,
        out IReadOnlyList<ResolvedBinding> resolvedBindings)
    {
        var findings = new List<MaterialAmbiguityFinding>(capacity: 3);
        var bindings = new List<ResolvedBinding>(capacity: 2);

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
        // candidates within the material spread. A single dominant
        // candidate (the only one above the floor, or one that beats the
        // runner-up by more than the material spread) is auto-resolved into a
        // binding instead — the caller should not have to round-trip a
        // clarification for an obvious single-candidate match.
        switch (ClassifyResolution(candidates.Datasets, options, "dataset.selection", CandidateKind.Dataset, out var datasetOptions, out var datasetBinding))
        {
            case ResolutionOutcome.Ambiguous:
                findings.Add(new MaterialAmbiguityFinding
                {
                    ReasonCode = ClarificationReasonCode.AmbiguousDataset,
                    QuestionId = "dataset.selection",
                    QuestionKind = ClarificationQuestionKind.SingleSelect,
                    Prompt = "Which dataset do you want to use?",
                    Options = datasetOptions
                });
                break;
            case ResolutionOutcome.AutoResolved:
                bindings.Add(datasetBinding!);
                break;
        }

        switch (ClassifyResolution(candidates.Processes, options, "process.selection", CandidateKind.Process, out var processOptions, out var processBinding))
        {
            case ResolutionOutcome.Ambiguous:
                findings.Add(new MaterialAmbiguityFinding
                {
                    ReasonCode = ClarificationReasonCode.AmbiguousProcess,
                    QuestionId = "process.selection",
                    QuestionKind = ClarificationQuestionKind.SingleSelect,
                    Prompt = "Which operation do you want to run?",
                    Options = processOptions
                });
                break;
            case ResolutionOutcome.AutoResolved:
                bindings.Add(processBinding!);
                break;
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

        resolvedBindings = bindings;
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
            return !candidates.Datasets.Any(candidate =>
                string.Equals(candidate.Id, pinnedId, StringComparison.Ordinal)
                && candidate.DatasetSubtype == DatasetSubtype.Service);
        }

        return candidates.Datasets.Count > 0
            && candidates.Datasets[0].ConfidenceBand == ConfidenceBand.High
            && candidates.Datasets[0].DatasetSubtype != DatasetSubtype.Service;
    }

    /// <summary>
    /// Outcome of classifying a single kind's candidate list.
    /// </summary>
    private enum ResolutionOutcome
    {
        /// <summary>No candidate cleared the high-confidence floor.</summary>
        None,

        /// <summary>
        /// Exactly one candidate is materially dominant — auto-bind it.
        /// </summary>
        AutoResolved,

        /// <summary>
        /// Two or more candidates are viable within the material spread — ask.
        /// </summary>
        Ambiguous
    }

    /// <summary>
    /// Classifies a kind's ranked candidates into auto-resolve / ambiguous /
    /// none. The candidates are pre-sorted by descending score
    /// (<see cref="DeterministicGroundingEngine"/>), so the lead is the top hit.
    ///
    /// <list type="bullet">
    /// <item>Lead below the high-confidence floor → <see cref="ResolutionOutcome.None"/>
    /// (the existing missing-/low-confidence paths own this case).</item>
    /// <item>Two or more candidates clear the floor within
    /// <see cref="GroundingOptions.MaterialSpread"/> of the lead →
    /// <see cref="ResolutionOutcome.Ambiguous"/>: genuinely competing
    /// interpretations, keep clarifying.</item>
    /// <item>Otherwise the lead is the sole dominant high-confidence candidate
    /// (either the only one above the floor, or it beats the runner-up by more
    /// than the material spread) → <see cref="ResolutionOutcome.AutoResolved"/>.</item>
    /// </list>
    ///
    /// Reusing <see cref="GroundingOptions.MaterialSpread"/> as the dominance
    /// margin keeps a single, conservatively-tuned knob: any runner-up close
    /// enough to make the pair ambiguous is also close enough to block the
    /// auto-bind, so the two paths can never both fire and a near-tie always
    /// errs toward asking rather than a wrong silent bind.
    /// </summary>
    private static ResolutionOutcome ClassifyResolution(
        IReadOnlyList<GroundingCandidate> candidates,
        GroundingOptions options,
        string questionId,
        CandidateKind kind,
        out IReadOnlyList<ClarificationOption> options_out,
        out ResolvedBinding? binding)
    {
        options_out = [];
        binding = null;

        if (candidates.Count == 0 || candidates[0].Score < options.HighConfidenceFloor)
        {
            return ResolutionOutcome.None;
        }

        var lead = candidates[0];
        var options_list = new List<ClarificationOption>(capacity: candidates.Count)
        {
            new() { Id = lead.Id, Label = lead.DisplayName ?? lead.Id }
        };

        for (var i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].Score < options.HighConfidenceFloor)
            {
                break;
            }

            if (lead.Score - candidates[i].Score > options.MaterialSpread)
            {
                break;
            }

            options_list.Add(new ClarificationOption
            {
                Id = candidates[i].Id,
                Label = candidates[i].DisplayName ?? candidates[i].Id
            });
        }

        if (options_list.Count >= 2)
        {
            options_out = options_list;
            return ResolutionOutcome.Ambiguous;
        }

        // Single dominant candidate: record the runner-up margin so the
        // auto-resolution stays auditable (1.0 when there was no runner-up).
        var runnerUpMargin = candidates.Count > 1
            ? lead.Score - candidates[1].Score
            : 1.0;

        binding = new ResolvedBinding
        {
            QuestionId = questionId,
            Kind = kind,
            CandidateId = lead.Id,
            DisplayName = lead.DisplayName,
            Score = lead.Score,
            RunnerUpMargin = Math.Round(runnerUpMargin, 3)
        };
        return ResolutionOutcome.AutoResolved;
    }
}
