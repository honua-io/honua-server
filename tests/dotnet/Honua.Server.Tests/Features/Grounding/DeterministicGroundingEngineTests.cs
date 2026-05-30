// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Grounding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Grounding;

/// <summary>
/// Unit tests for the deterministic workflow-family classifier and the
/// weighted bag-of-lemma candidate ranker. These pin the canonical outputs the
/// conformance harness (honua-server-734) replays against fixtures.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class DeterministicGroundingEngineTests
{
    private readonly DeterministicGroundingEngine _engine = new();
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    public void Name_IsDeterministicSoConformanceFixturesCanPinIt()
    {
        _engine.Name.Should().Be("deterministic");
    }

    // -----------------------------------------------------------------------
    // Classify — workflow family detection
    // -----------------------------------------------------------------------

    [UnitTest]
    public void Classify_WithWorkflowFamilyHint_HonorsHintAtFullConfidence()
    {
        var request = new GroundingRequest
        {
            Goal = "anything at all",
            WorkflowFamilyHint = WorkflowFamily.PublishData
        };

        var classification = _engine.Classify(request);

        classification.Value.Should().Be(WorkflowFamily.PublishData);
        classification.Confidence.Should().Be(1.0);
        classification.Evidence.Should().ContainSingle().Which.Should().Be("hint");
    }

    [UnitTest]
    public void Classify_EmptyGoal_ReturnsZeroConfidenceFallback()
    {
        var request = new GroundingRequest { Goal = "   " };

        var classification = _engine.Classify(request);

        classification.Confidence.Should().Be(0.0);
        classification.Evidence.Should().ContainSingle().Which.Should().Be("empty");
    }

    [UnitTest]
    public void Classify_VerbFromAnalyzeFamily_ClassifiesAsAnalyze()
    {
        var request = new GroundingRequest { Goal = "Buffer the roads by 50 meters" };

        var classification = _engine.Classify(request);

        classification.Value.Should().Be(WorkflowFamily.Analyze);
        classification.Confidence.Should().BeGreaterThan(0.5);
        classification.Evidence.Should().Contain(e => e.Contains("Analyze"));
    }

    [UnitTest]
    public void Classify_VerbFromPublishFamily_ClassifiesAsPublishData()
    {
        var request = new GroundingRequest { Goal = "Publish parcels as a feature service" };

        var classification = _engine.Classify(request);

        classification.Value.Should().Be(WorkflowFamily.PublishData);
        classification.Confidence.Should().BeGreaterThan(0.0);
    }

    [UnitTest]
    public void Classify_VerbFromBuildAppFamily_ClassifiesAsBuildApp()
    {
        var request = new GroundingRequest { Goal = "Compose a dashboard for incidents" };

        var classification = _engine.Classify(request);

        classification.Value.Should().Be(WorkflowFamily.BuildApp);
    }

    [UnitTest]
    public void Classify_VerbFromAutomateDeployFamily_ClassifiesAsAutomateDeploy()
    {
        var request = new GroundingRequest { Goal = "Deploy the new pipeline to production" };

        var classification = _engine.Classify(request);

        classification.Value.Should().Be(WorkflowFamily.AutomateDeploy);
    }

    [UnitTest]
    public void Classify_UnrecognisedGoal_FallsBackToAnalyzeWithLowConfidence()
    {
        var request = new GroundingRequest { Goal = "something abstract" };

        var classification = _engine.Classify(request);

        classification.Value.Should().Be(WorkflowFamily.Analyze);
        classification.Confidence.Should().BeLessThan(0.6);
        classification.Evidence.Should().Contain("fallback:analyze");
    }

    [UnitTest]
    public void Classify_ScoresDeterministicAcrossRuns()
    {
        var request = new GroundingRequest { Goal = "buffer and intersect the parcels" };

        var a = _engine.Classify(request);
        var b = _engine.Classify(request);

        a.Should().BeEquivalentTo(b);
    }

    // -----------------------------------------------------------------------
    // ScoreProcesses — catalog ranking
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ScoreProcesses_BufferGoal_RanksBufferProcessFirst()
    {
        var request = new GroundingRequest { Goal = "Buffer the highways by 100 meters" };

        var candidates = _engine.ScoreProcesses(request, _catalog.ListProcesses());

        candidates.Should().NotBeEmpty();
        candidates[0].Id.Should().Contain("buffer");
        candidates[0].Kind.Should().Be(CandidateKind.Process);
        candidates[0].Score.Should().BeGreaterThan(0.0);
    }

    [UnitTest]
    public void ScoreProcesses_SortsCandidatesByDescendingScore()
    {
        var request = new GroundingRequest { Goal = "Simplify the shoreline polygons" };

        var candidates = _engine.ScoreProcesses(request, _catalog.ListProcesses());

        candidates.Should().NotBeEmpty();
        candidates.Should().BeInDescendingOrder(c => c.Score);
    }

    [UnitTest]
    public void ScoreProcesses_EmptyCatalog_ReturnsEmpty()
    {
        var request = new GroundingRequest { Goal = "Buffer the highways" };

        var candidates = _engine.ScoreProcesses(request, []);

        candidates.Should().BeEmpty();
    }

    [UnitTest]
    public void ScoreProcesses_EmptyGoal_ReturnsEmpty()
    {
        var request = new GroundingRequest { Goal = "   " };

        var candidates = _engine.ScoreProcesses(request, _catalog.ListProcesses());

        candidates.Should().BeEmpty();
    }

    [UnitTest]
    public void ScoreProcesses_NonMatchingGoal_ReturnsEmpty()
    {
        var request = new GroundingRequest { Goal = "xylophone quartz zebra" };

        var candidates = _engine.ScoreProcesses(request, _catalog.ListProcesses());

        candidates.Should().BeEmpty();
    }

    [UnitTest]
    public void ScoreProcesses_AttachesEvidenceTagsForExplainability()
    {
        var request = new GroundingRequest { Goal = "Buffer the highways" };

        var candidates = _engine.ScoreProcesses(request, _catalog.ListProcesses());

        candidates[0].Evidence.Should().NotBeEmpty();
        candidates[0].Evidence.Should().Contain(e => e.StartsWith("title:"));
    }

    // -----------------------------------------------------------------------
    // ScoreLayers / ScoreServices — catalog text ranking
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ScoreLayers_RanksLayersByNameOverlap()
    {
        var request = new GroundingRequest { Goal = "Measure incidents per district" };
        var layers = new List<LayerCandidate>
        {
            new(1, "Incidents", "Point features for reported incidents"),
            new(2, "Districts", "Administrative district polygons"),
            new(3, "Unrelated", "Something else entirely")
        };

        var results = _engine.ScoreLayers(request, layers);

        results.Should().HaveCountGreaterOrEqualTo(2);
        results[0].Kind.Should().Be(CandidateKind.Dataset);
        results.Select(c => c.Id).Should().Contain("1").And.Contain("2");
        results.Should().NotContain(c => c.Id == "3");
    }

    [UnitTest]
    public void ScoreLayers_EmptyInputs_ReturnsEmpty()
    {
        var request = new GroundingRequest { Goal = "Anything" };

        _engine.ScoreLayers(request, []).Should().BeEmpty();

        var emptyGoal = new GroundingRequest { Goal = "  " };
        _engine.ScoreLayers(emptyGoal, [new LayerCandidate(1, "Anything", null)])
            .Should().BeEmpty();
    }

    [UnitTest]
    public void ScoreServices_RanksServicesByNameOverlap()
    {
        var request = new GroundingRequest { Goal = "Show parcels service" };
        var services = new List<ServiceCandidate>
        {
            new("Parcels", "County parcel feature service"),
            new("Buildings", "Building footprints feature service")
        };

        var results = _engine.ScoreServices(request, services);

        results.Should().NotBeEmpty();
        results[0].Id.Should().Be("Parcels");
    }

    // -----------------------------------------------------------------------
    // Deterministic tie-breakers — scores are rounded to 3 decimals before
    // ranking, so equal-score candidates must fall back to an ordinal key so
    // the published contract's deterministic ranking still holds.
    // -----------------------------------------------------------------------

    [UnitTest]
    public void ScoreLayers_WithTiedScores_SortsStablyByOrdinalIdForDeterminism()
    {
        var request = new GroundingRequest { Goal = "water" };
        var layersForward = new List<LayerCandidate>
        {
            new(10, "water", null),
            new(2, "water", null),
            new(7, "water", null)
        };
        var layersReversed = new List<LayerCandidate>
        {
            new(7, "water", null),
            new(10, "water", null),
            new(2, "water", null)
        };

        var forward = _engine.ScoreLayers(request, layersForward);
        var reversed = _engine.ScoreLayers(request, layersReversed);

        forward.Select(c => c.Score).Should().OnlyContain(s => s == forward[0].Score);
        forward.Select(c => c.Id).Should().Equal(reversed.Select(c => c.Id));
    }

    [UnitTest]
    public void ScoreServices_WithTiedScores_SortsStablyByOrdinalIdForDeterminism()
    {
        var request = new GroundingRequest { Goal = "water" };
        var servicesForward = new List<ServiceCandidate>
        {
            new("water-c", null),
            new("water-a", null),
            new("water-b", null)
        };
        var servicesReversed = new List<ServiceCandidate>
        {
            new("water-b", null),
            new("water-c", null),
            new("water-a", null)
        };

        var forward = _engine.ScoreServices(request, servicesForward);
        var reversed = _engine.ScoreServices(request, servicesReversed);

        forward.Select(c => c.Id).Should().Equal("water-a", "water-b", "water-c");
        forward.Select(c => c.Id).Should().Equal(reversed.Select(c => c.Id));
    }
}
