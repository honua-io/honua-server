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
/// Unit tests for <see cref="IntentDrafter"/>. Covers the shape of the
/// canonical <see cref="DraftIntent"/> envelope for every workflow family,
/// including the BuildApp / AutomateDeploy stubs that ship as schema-stable
/// envelopes pending their typed-intent domains.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class IntentDrafterTests
{
    [UnitTest]
    public void Draft_AnalyzeFamily_EmitsAnalysisIntentWithEchoedGoalAndInputs()
    {
        var request = new GroundingRequest
        {
            Goal = "Buffer the roads by 50 meters",
            Constraints = new IntentConstraints { SpatialReferenceId = 3857, Units = "meters" },
            ExplicitInputs = ["layer-1", "layer-2"]
        };

        var draft = IntentDrafter.Draft(
            request,
            intentId: "intent-abc",
            classification: Classification(WorkflowFamily.Analyze, 0.9),
            candidates: new CandidateRanking(),
            clarificationQuestionIds: ["workflow_family"],
            clarificationsAnswered: [],
            assumptions: ["srid=3857"]);

        draft.IntentId.Should().Be("intent-abc");
        draft.Goal.Should().Be(request.Goal);
        draft.WorkflowFamily.Should().Be(WorkflowFamily.Analyze);
        draft.Analysis.Should().NotBeNull();
        draft.Analysis!.IntentId.Should().Be("intent-abc");
        draft.Analysis.Goal.Should().Be(request.Goal);
        draft.Analysis.Inputs.Should().BeEquivalentTo("layer-1", "layer-2");
        draft.Analysis.Constraints!.SpatialReferenceId.Should().Be(3857);
        draft.Publishing.Should().BeNull();
        draft.Provenance.ClarificationsAsked.Should().ContainSingle().Which.Should().Be("workflow_family");
        draft.Provenance.Assumptions.Should().ContainSingle().Which.Should().Be("srid=3857");
    }

    [UnitTest]
    public void Draft_PublishFamilyWithExplicitInput_EmitsPublishIntentFromExplicitSource()
    {
        var request = new GroundingRequest
        {
            Goal = "Publish the parcels layer",
            ExplicitInputs = ["parcels-layer"]
        };

        var draft = IntentDrafter.Draft(
            request,
            intentId: "intent-pub",
            classification: Classification(WorkflowFamily.PublishData, 1.0),
            candidates: new CandidateRanking(),
            clarificationQuestionIds: [],
            clarificationsAnswered: [],
            assumptions: []);

        draft.WorkflowFamily.Should().Be(WorkflowFamily.PublishData);
        draft.Analysis.Should().BeNull();
        draft.Publishing.Should().NotBeNull();
        draft.Publishing!.SourceId.Should().Be("parcels-layer");
        draft.Publishing.SourceKind.Should().Be(PublishSourceKind.FeatureLayer);
        draft.Publishing.TargetKind.Should().Be(PublishTargetKind.FeatureService);
        draft.Publishing.Status.Should().Be(PublishIntentStatus.Draft);
    }

    [UnitTest]
    public void Draft_PublishFamilyWithHighConfidenceDataset_UsesTopDatasetAsSource()
    {
        var request = new GroundingRequest { Goal = "Publish parcels" };
        var candidates = new CandidateRanking
        {
            Datasets =
            [
                new GroundingCandidate
                {
                    Id = "parcels",
                    Kind = CandidateKind.Dataset,
                    Score = 0.95,
                    ConfidenceBand = ConfidenceBand.High
                }
            ]
        };

        var draft = IntentDrafter.Draft(
            request,
            intentId: "intent-pub",
            classification: Classification(WorkflowFamily.PublishData, 1.0),
            candidates: candidates,
            clarificationQuestionIds: [],
            clarificationsAnswered: [],
            assumptions: []);

        draft.Publishing.Should().NotBeNull();
        draft.Publishing!.SourceId.Should().Be("parcels");
    }

    [UnitTest]
    public void Draft_PublishFamilyWithoutSource_OmitsPublishingBlock()
    {
        var request = new GroundingRequest { Goal = "Publish some data somewhere" };

        var draft = IntentDrafter.Draft(
            request,
            intentId: "intent-pub-empty",
            classification: Classification(WorkflowFamily.PublishData, 1.0),
            candidates: new CandidateRanking(),
            clarificationQuestionIds: [],
            clarificationsAnswered: [],
            assumptions: []);

        draft.Publishing.Should().BeNull();
        draft.Analysis.Should().BeNull();
    }

    [UnitTest]
    public void Draft_BuildAppFamily_EmitsEnvelopeStubWithoutTypedIntent()
    {
        var request = new GroundingRequest { Goal = "Build a dashboard for incidents" };

        var draft = IntentDrafter.Draft(
            request,
            intentId: "intent-app",
            classification: Classification(WorkflowFamily.BuildApp, 1.0),
            candidates: new CandidateRanking(),
            clarificationQuestionIds: [],
            clarificationsAnswered: [],
            assumptions: []);

        draft.WorkflowFamily.Should().Be(WorkflowFamily.BuildApp);
        draft.Analysis.Should().BeNull();
        draft.Publishing.Should().BeNull();
        draft.RequestedOutputs.Should().ContainSingle().Which.Should().Be(ArtifactKind.AppBundle);
    }

    [UnitTest]
    public void Draft_AutomateDeployFamily_EmitsEnvelopeStubWithoutTypedIntent()
    {
        var request = new GroundingRequest { Goal = "Deploy the pipeline to production" };

        var draft = IntentDrafter.Draft(
            request,
            intentId: "intent-deploy",
            classification: Classification(WorkflowFamily.AutomateDeploy, 1.0),
            candidates: new CandidateRanking(),
            clarificationQuestionIds: [],
            clarificationsAnswered: [],
            assumptions: []);

        draft.WorkflowFamily.Should().Be(WorkflowFamily.AutomateDeploy);
        draft.Analysis.Should().BeNull();
        draft.Publishing.Should().BeNull();
    }

    [UnitTest]
    public void Draft_InfersRequestedOutputsFromGoalTokens()
    {
        var mapRequest = new GroundingRequest { Goal = "Produce a map of incidents" };
        IntentDrafter.Draft(mapRequest, "i1", Classification(WorkflowFamily.Analyze, 0.9), new CandidateRanking(), [], [], [])
            .RequestedOutputs.Should().Contain(ArtifactKind.Map);

        var countRequest = new GroundingRequest { Goal = "Count the incidents" };
        IntentDrafter.Draft(countRequest, "i2", Classification(WorkflowFamily.Analyze, 0.9), new CandidateRanking(), [], [], [])
            .RequestedOutputs.Should().Contain(ArtifactKind.Scalar);

        var reportRequest = new GroundingRequest { Goal = "Produce a report of incidents" };
        IntentDrafter.Draft(reportRequest, "i3", Classification(WorkflowFamily.Analyze, 0.9), new CandidateRanking(), [], [], [])
            .RequestedOutputs.Should().Contain(ArtifactKind.Report);
    }

    [UnitTest]
    public void Draft_ProvenanceMirrorsRankingCandidatesAndClarificationQuestions()
    {
        var candidates = new CandidateRanking
        {
            Datasets =
            [
                new GroundingCandidate
                {
                    Id = "layer-a",
                    Kind = CandidateKind.Dataset,
                    DisplayName = "Layer A",
                    Score = 0.9,
                    ConfidenceBand = ConfidenceBand.High
                }
            ],
            Processes =
            [
                new GroundingCandidate
                {
                    Id = "geometry.buffer",
                    Kind = CandidateKind.Process,
                    Score = 0.9,
                    ConfidenceBand = ConfidenceBand.High
                }
            ]
        };

        var draft = IntentDrafter.Draft(
            new GroundingRequest { Goal = "Buffer the parcels" },
            intentId: "intent-p",
            classification: Classification(WorkflowFamily.Analyze, 0.9),
            candidates: candidates,
            clarificationQuestionIds: ["q1", "q2"],
            clarificationsAnswered: ["q1"],
            assumptions: ["srid=4326 (default)"]);

        draft.Provenance.Sources.Should().ContainSingle()
            .Which.SourceId.Should().Be("layer-a");
        draft.Provenance.ProcessDefinitions.Should().ContainSingle()
            .Which.Should().Be("geometry.buffer");
        draft.Provenance.ClarificationsAsked.Should().BeEquivalentTo("q1", "q2");
        draft.Provenance.ClarificationsAnswered.Should().ContainSingle()
            .Which.Should().Be("q1");
        draft.Provenance.Assumptions.Should().ContainSingle()
            .Which.Should().Be("srid=4326 (default)");
    }

    [UnitTest]
    public void Draft_ClarificationsAnswered_AreSortedOrdinallyEvenWhenInputIsAHashSet()
    {
        // Regression: GroundingService passes a HashSet of applied question
        // ids into the drafter. HashSet enumeration order is unspecified, so
        // the drafter must sort before assigning ProvenanceRecord.ClarificationsAnswered
        // to honour the "fixed request + catalog snapshot" determinism contract
        // documented in docs/developer/GROUNDING.md.
        var unsortedAnswers = new HashSet<string>(StringComparer.Ordinal)
        {
            "publish.target",
            "workflow_family",
            "param.distance",
            "dataset.selection"
        };

        var draft = IntentDrafter.Draft(
            new GroundingRequest { Goal = "Buffer the parcels" },
            intentId: "intent-stable",
            classification: Classification(WorkflowFamily.Analyze, 0.9),
            candidates: new CandidateRanking(),
            clarificationQuestionIds: [],
            clarificationsAnswered: unsortedAnswers,
            assumptions: []);

        draft.Provenance.ClarificationsAnswered.Should().ContainInOrder(
            "dataset.selection",
            "param.distance",
            "publish.target",
            "workflow_family");
    }

    private static WorkflowFamilyClassification Classification(WorkflowFamily family, double confidence) => new()
    {
        Value = family,
        Confidence = confidence
    };
}
