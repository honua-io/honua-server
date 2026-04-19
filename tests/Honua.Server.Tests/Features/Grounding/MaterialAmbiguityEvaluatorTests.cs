// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Grounding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Grounding;

/// <summary>
/// Pins the ADR-0027 material-ambiguity rule set. Each rule has at least one
/// positive and one negative case so a refactor that drops a finding trips
/// here rather than in a downstream conformance run.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class MaterialAmbiguityEvaluatorTests
{
    private readonly GroundingOptions _options = new();

    private static readonly GroundingRequest BaselineRequest = new() { Goal = "Buffer the roads by 50 meters" };

    // -----------------------------------------------------------------------
    // LowConfidence (workflow-family floor)
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Evaluate_LowClassificationConfidence_SurfacesLowConfidenceFirst()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.Analyze,
            Confidence = 0.1
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        findings.Should().NotBeEmpty();
        findings[0].ReasonCode.Should().Be(ClarificationReasonCode.LowConfidence);
        findings[0].QuestionId.Should().Be("workflow_family");
        findings[0].QuestionKind.Should().Be(ClarificationQuestionKind.SingleSelect);
        findings[0].Options.Should().NotBeNull();
        findings[0].Options!.Select(o => o.Id).Should().BeEquivalentTo(
            nameof(WorkflowFamily.Analyze),
            nameof(WorkflowFamily.PublishData),
            nameof(WorkflowFamily.BuildApp),
            nameof(WorkflowFamily.AutomateDeploy));
    }

    [UnitTest]
    public void Evaluate_HighClassificationConfidence_DoesNotEmitLowConfidence()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.Analyze,
            Confidence = 0.9
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        findings.Should().NotContain(f => f.ReasonCode == ClarificationReasonCode.LowConfidence);
    }

    // -----------------------------------------------------------------------
    // MissingRequiredInput
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Evaluate_UnmetRequiredParameters_SurfaceAsMissingRequiredInputFindings()
    {
        var gaps = new[]
        {
            new ProcessParameterSpec
            {
                Name = "distance",
                DisplayName = "Buffer distance",
                Description = "Buffer distance in meters.",
                ValueType = ProcessParameterValueType.FloatingPoint,
                Required = true
            }
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            new CandidateRanking(),
            gaps,
            _options);

        findings.Should().ContainSingle(f => f.ReasonCode == ClarificationReasonCode.MissingRequiredInput)
            .Which.QuestionId.Should().Be("param.distance");
        findings.First(f => f.ReasonCode == ClarificationReasonCode.MissingRequiredInput)
            .QuestionKind.Should().Be(ClarificationQuestionKind.FreeText);
    }

    // -----------------------------------------------------------------------
    // AmbiguousDataset / AmbiguousProcess
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Evaluate_TwoHighConfidenceDatasetsWithinSpread_SurfacesAmbiguousDataset()
    {
        var ranking = new CandidateRanking
        {
            Datasets =
            [
                Candidate("ds-a", 0.92, CandidateKind.Dataset),
                Candidate("ds-b", 0.90, CandidateKind.Dataset)
            ]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            ranking,
            requiredParameterGaps: [],
            _options);

        findings.Should().Contain(f => f.ReasonCode == ClarificationReasonCode.AmbiguousDataset);
        var finding = findings.First(f => f.ReasonCode == ClarificationReasonCode.AmbiguousDataset);
        finding.Options.Should().NotBeNull();
        finding.Options!.Select(o => o.Id).Should().BeEquivalentTo("ds-a", "ds-b");
    }

    [UnitTest]
    public void Evaluate_DatasetLeadBeyondMaterialSpread_DoesNotSurfaceAmbiguous()
    {
        var ranking = new CandidateRanking
        {
            Datasets =
            [
                Candidate("ds-a", 0.95, CandidateKind.Dataset),
                Candidate("ds-b", 0.72, CandidateKind.Dataset)
            ]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            ranking,
            requiredParameterGaps: [],
            _options);

        findings.Should().NotContain(f => f.ReasonCode == ClarificationReasonCode.AmbiguousDataset);
    }

    [UnitTest]
    public void Evaluate_TopDatasetBelowHighConfidenceFloor_DoesNotSurfaceAmbiguous()
    {
        var ranking = new CandidateRanking
        {
            Datasets =
            [
                Candidate("ds-a", 0.55, CandidateKind.Dataset),
                Candidate("ds-b", 0.55, CandidateKind.Dataset)
            ]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            ranking,
            requiredParameterGaps: [],
            _options);

        findings.Should().NotContain(f => f.ReasonCode == ClarificationReasonCode.AmbiguousDataset);
    }

    [UnitTest]
    public void Evaluate_TwoHighConfidenceProcessesWithinSpread_SurfacesAmbiguousProcess()
    {
        var ranking = new CandidateRanking
        {
            Processes =
            [
                Candidate("geometry.buffer", 0.91, CandidateKind.Process),
                Candidate("geometry.project", 0.88, CandidateKind.Process)
            ]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            ranking,
            requiredParameterGaps: [],
            _options);

        findings.Should().Contain(f => f.ReasonCode == ClarificationReasonCode.AmbiguousProcess);
    }

    // -----------------------------------------------------------------------
    // DestructiveAction
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Evaluate_DestructiveTopProcess_SurfacesDestructiveActionConfirmation()
    {
        var ranking = new CandidateRanking
        {
            Processes = [Candidate("data-management.delete-features", 0.9, CandidateKind.Process)]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            ranking,
            requiredParameterGaps: [],
            _options);

        findings.Should().Contain(f => f.ReasonCode == ClarificationReasonCode.DestructiveAction);
        findings.First(f => f.ReasonCode == ClarificationReasonCode.DestructiveAction)
            .QuestionKind.Should().Be(ClarificationQuestionKind.Confirmation);
    }

    [UnitTest]
    public void Evaluate_NonDestructiveTopProcess_DoesNotSurfaceDestructiveAction()
    {
        var ranking = new CandidateRanking
        {
            Processes = [Candidate("geometry.buffer", 0.9, CandidateKind.Process)]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            ranking,
            requiredParameterGaps: [],
            _options);

        findings.Should().NotContain(f => f.ReasonCode == ClarificationReasonCode.DestructiveAction);
    }

    // -----------------------------------------------------------------------
    // PublishAction
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Evaluate_PublishWithNoResolvableSource_SurfacesPublishSourceFreeText()
    {
        // Finding #2 regression: when PublishData has no ExplicitInputs and
        // no high-confidence dataset lead, the evaluator must ask for the
        // source. Without this, answering publish.target on a follow-up turn
        // leaves draftIntent.publishing == null with no clarification — a
        // terminal dead state.
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        var sourceFinding = findings.FirstOrDefault(f => f.QuestionId == "publish.source");
        sourceFinding.Should().NotBeNull();
        sourceFinding!.ReasonCode.Should().Be(ClarificationReasonCode.MissingRequiredInput);
        sourceFinding.QuestionKind.Should().Be(ClarificationQuestionKind.FreeText);
        sourceFinding.Options.Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_PublishWithDatasetCandidates_SurfacesPublishSourceAsSingleSelect()
    {
        // When dataset candidates exist but none reached the high-confidence
        // floor, publish.source is a single-select picker over the current
        // ranking so the operator can pin a candidate in a single turn.
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        };

        var ranking = new CandidateRanking
        {
            Datasets =
            [
                Candidate("ds-a", 0.5, CandidateKind.Dataset) with { ConfidenceBand = ConfidenceBand.Medium },
                Candidate("ds-b", 0.4, CandidateKind.Dataset) with { ConfidenceBand = ConfidenceBand.Medium }
            ]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            ranking,
            requiredParameterGaps: [],
            _options);

        var sourceFinding = findings.First(f => f.QuestionId == "publish.source");
        sourceFinding.QuestionKind.Should().Be(ClarificationQuestionKind.SingleSelect);
        sourceFinding.Options.Should().NotBeNull();
        sourceFinding.Options!.Select(o => o.Id).Should().ContainInOrder("ds-a", "ds-b");
    }

    [UnitTest]
    public void Evaluate_PublishWithHighConfidenceDataset_DoesNotAskForSource()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        };

        var ranking = new CandidateRanking
        {
            Datasets = [Candidate("ds-a", 0.95, CandidateKind.Dataset)]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            ranking,
            requiredParameterGaps: [],
            _options);

        findings.Should().NotContain(f => f.QuestionId == "publish.source");
    }

    [UnitTest]
    public void Evaluate_PublishWithExplicitInput_DoesNotAskForSource()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            new GroundingRequest { Goal = "Publish parcels", ExplicitInputs = ["parcels"] },
            classification,
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        findings.Should().NotContain(f => f.QuestionId == "publish.source");
    }

    [UnitTest]
    public void Evaluate_PublishWorkflowFamily_SurfacesPublishActionWithAllTargets()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        findings.Should().Contain(f => f.ReasonCode == ClarificationReasonCode.PublishAction);
        var finding = findings.First(f => f.ReasonCode == ClarificationReasonCode.PublishAction);
        finding.Options.Should().NotBeNull();
        finding.Options!.Select(o => o.Id).Should().BeEquivalentTo(
            nameof(PublishTargetKind.FeatureService),
            nameof(PublishTargetKind.TileService),
            nameof(PublishTargetKind.MapService),
            nameof(PublishTargetKind.StaticExport));
    }

    // -----------------------------------------------------------------------
    // PolicyBoundary
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Evaluate_BuildAppFamily_SurfacesPolicyBoundary()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.BuildApp,
            Confidence = 1.0
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        findings.Should().Contain(f => f.ReasonCode == ClarificationReasonCode.PolicyBoundary);
        findings.First(f => f.ReasonCode == ClarificationReasonCode.PolicyBoundary)
            .QuestionId.Should().Be("workflow_family.blocked");
    }

    [UnitTest]
    public void Evaluate_AutomateDeployFamily_SurfacesPolicyBoundary()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.AutomateDeploy,
            Confidence = 1.0
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        findings.Should().Contain(f => f.ReasonCode == ClarificationReasonCode.PolicyBoundary);
    }

    [UnitTest]
    public void Evaluate_AnalyzeFamily_DoesNotSurfacePolicyBoundary()
    {
        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            HighConfidenceAnalyze(),
            new CandidateRanking(),
            requiredParameterGaps: [],
            _options);

        findings.Should().NotContain(f => f.ReasonCode == ClarificationReasonCode.PolicyBoundary);
    }

    // -----------------------------------------------------------------------
    // Deterministic ordering across all findings
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Evaluate_CombinedFindings_PreserveDocumentedOrder()
    {
        var classification = new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 0.1 // triggers LowConfidence
        };

        var gaps = new[]
        {
            new ProcessParameterSpec
            {
                Name = "distance",
                DisplayName = "Buffer distance",
                Description = "meters",
                ValueType = ProcessParameterValueType.FloatingPoint,
                Required = true
            }
        };

        var ranking = new CandidateRanking
        {
            Datasets =
            [
                Candidate("ds-a", 0.92, CandidateKind.Dataset),
                Candidate("ds-b", 0.90, CandidateKind.Dataset)
            ],
            Processes = [Candidate("data-management.delete-features", 0.9, CandidateKind.Process)]
        };

        var findings = MaterialAmbiguityEvaluator.Evaluate(
            BaselineRequest,
            classification,
            ranking,
            gaps,
            _options);

        findings.Select(f => f.ReasonCode).Should().ContainInOrder(
            ClarificationReasonCode.LowConfidence,
            ClarificationReasonCode.MissingRequiredInput,
            ClarificationReasonCode.AmbiguousDataset,
            ClarificationReasonCode.DestructiveAction,
            ClarificationReasonCode.PublishAction);
    }

    private static WorkflowFamilyClassification HighConfidenceAnalyze() => new()
    {
        Value = WorkflowFamily.Analyze,
        Confidence = 0.9
    };

    private static GroundingCandidate Candidate(string id, double score, CandidateKind kind) => new()
    {
        Id = id,
        Kind = kind,
        DisplayName = id,
        Score = score,
        ConfidenceBand = ConfidenceBand.High
    };
}
