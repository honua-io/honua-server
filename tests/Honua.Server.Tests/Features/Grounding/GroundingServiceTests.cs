// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Grounding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Grounding;

/// <summary>
/// End-to-end unit tests for <see cref="GroundingService"/>. The engine and
/// authorization filter are stubbed so each test pins one orchestration
/// concern — banding/capping, authorization filtering, parameter-gap probing,
/// clarification envelope wiring, layer catalog outage handling, and error
/// translation.
/// </summary>
[Protocol(Protocols.Mcp)]
public sealed class GroundingServiceTests
{
    private readonly IGroundingEngine _engine = Substitute.For<IGroundingEngine>();
    private readonly IProcessCatalog _processCatalog = Substitute.For<IProcessCatalog>();
    private readonly IGroundingAuthorizationFilter _authorizationFilter = Substitute.For<IGroundingAuthorizationFilter>();
    private readonly ILayerCatalog _layerCatalog = Substitute.For<ILayerCatalog>();
    private readonly GroundingOptions _options = new();

    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "op")], "Test"));

    public GroundingServiceTests()
    {
        _engine.Name.Returns("test-engine");
        _processCatalog.ListProcesses().Returns([]);
        _layerCatalog.ListLayersAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LayerDefinition>());
        _layerCatalog.ListServicesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ServiceDefinition>());
        _authorizationFilter.Filter(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<GroundingCandidate>>())
            .Returns(callInfo => callInfo.Arg<IReadOnlyList<GroundingCandidate>>());
    }

    [UnitTest]
    public async Task GroundAsync_EmptyGoal_ThrowsEmptyGoalGroundingException()
    {
        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest { Goal = "   " }, Principal);

        var ex = (await act.Should().ThrowAsync<GroundingException>()).Which;
        ex.Kind.Should().Be(GroundingErrorKind.EmptyGoal);
    }

    [UnitTest]
    public async Task GroundAsync_NullRequest_ThrowsArgumentNullException()
    {
        var service = CreateService();

        var act = async () => await service.GroundAsync(null!, Principal);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [UnitTest]
    public async Task GroundAsync_NullPrincipal_ThrowsArgumentNullException()
    {
        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest { Goal = "anything" }, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [UnitTest]
    public async Task GroundAsync_UsesEngineForClassification()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.Analyze,
            Confidence = 0.9
        });

        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.WorkflowFamily.Value.Should().Be(WorkflowFamily.Analyze);
        result.Engine.Should().Be("test-engine");
        _engine.Received(1).Classify(Arg.Any<GroundingRequest>());
    }

    [UnitTest]
    public async Task GroundAsync_AppliesConfidenceBandsFromConfiguredThresholds()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns(
            [
                Candidate("p-high", 0.80, CandidateKind.Process),
                Candidate("p-medium", 0.50, CandidateKind.Process),
                Candidate("p-low", 0.10, CandidateKind.Process)
            ]);

        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.Candidates.Processes.Should().HaveCount(3);
        result.Candidates.Processes[0].ConfidenceBand.Should().Be(ConfidenceBand.High);
        result.Candidates.Processes[1].ConfidenceBand.Should().Be(ConfidenceBand.Medium);
        result.Candidates.Processes[2].ConfidenceBand.Should().Be(ConfidenceBand.Low);
    }

    [UnitTest]
    public async Task GroundAsync_CapsCandidatesPerKindFromOptions()
    {
        _options.MaxCandidatesPerKind = 2;
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns(
            [
                Candidate("p1", 0.9, CandidateKind.Process),
                Candidate("p2", 0.8, CandidateKind.Process),
                Candidate("p3", 0.7, CandidateKind.Process),
                Candidate("p4", 0.6, CandidateKind.Process)
            ]);

        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.Candidates.Processes.Should().HaveCount(2);
        result.Candidates.Processes.Select(c => c.Id).Should().ContainInOrder("p1", "p2");
    }

    [UnitTest]
    public async Task GroundAsync_FiltersCandidatesThroughAuthorizationFilter()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("allowed", 0.9, CandidateKind.Process), Candidate("denied", 0.8, CandidateKind.Process)]);
        _authorizationFilter.Filter(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<GroundingCandidate>>())
            .Returns(callInfo =>
                callInfo.Arg<IReadOnlyList<GroundingCandidate>>()
                    .Where(c => c.Id == "allowed")
                    .ToList());

        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.Candidates.Processes.Should().ContainSingle().Which.Id.Should().Be("allowed");
    }

    [UnitTest]
    public async Task GroundAsync_ProbesOnlyTopProcessCandidateForRequiredParameters()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("geometry.buffer", 0.9, CandidateKind.Process)]);

        _processCatalog.GetProcess("geometry.buffer").Returns(new ProcessDefinition
        {
            ProcessId = "geometry.buffer",
            Title = "Buffer",
            Description = "Buffers geometries.",
            Category = "geometry",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "distance",
                    DisplayName = "Buffer distance",
                    Description = "meters",
                    ValueType = ProcessParameterValueType.FloatingPoint,
                    Required = true
                }
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest { Goal = "Buffer the roads" },
            Principal);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.ReasonCodes.Should().Contain(ClarificationReasonCode.MissingRequiredInput);
        result.Clarification.Questions.Should().Contain(q => q.QuestionId == "param.distance");
    }

    [UnitTest]
    public async Task GroundAsync_ParameterMentionedInGoal_IsNotTreatedAsGap()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("geometry.buffer", 0.9, CandidateKind.Process)]);

        _processCatalog.GetProcess("geometry.buffer").Returns(new ProcessDefinition
        {
            ProcessId = "geometry.buffer",
            Title = "Buffer",
            Description = "Buffers geometries.",
            Category = "geometry",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "distance",
                    DisplayName = "Buffer distance",
                    Description = "meters",
                    ValueType = ProcessParameterValueType.FloatingPoint,
                    Required = true
                }
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest { Goal = "buffer by distance of 50 meters" },
            Principal);

        if (result.Clarification is not null)
        {
            result.Clarification.Questions.Should().NotContain(q => q.QuestionId == "param.distance");
        }
    }

    [UnitTest]
    public async Task GroundAsync_SridConstraintSatisfiesRequiredSridParameter()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("geometry.project", 0.9, CandidateKind.Process)]);
        _processCatalog.GetProcess("geometry.project").Returns(new ProcessDefinition
        {
            ProcessId = "geometry.project",
            Title = "Project",
            Description = "Projects geometries to a target SRID.",
            Category = "geometry",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "srid",
                    DisplayName = "SRID",
                    Description = "Target SRID",
                    ValueType = ProcessParameterValueType.Srid,
                    Required = true
                }
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "project the geometries",
                Constraints = new IntentConstraints { SpatialReferenceId = 3857 }
            },
            Principal);

        if (result.Clarification is not null)
        {
            result.Clarification.Questions.Should().NotContain(q => q.QuestionId == "param.srid");
        }
    }

    [UnitTest]
    public async Task GroundAsync_MissingLayerCatalog_ReturnsEmptyDatasetsAndNoFailure()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);

        var service = CreateServiceWithoutLayerCatalog();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.Candidates.Datasets.Should().BeEmpty();
    }

    [UnitTest]
    public async Task GroundAsync_LayerCatalogThrows_ReturnsEmptyDatasetsWithoutPropagatingException()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _layerCatalog.ListLayersAsync(Arg.Any<CancellationToken>())
            .Returns<Task<LayerDefinition[]>>(_ => throw new InvalidOperationException("catalog down"));

        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "incidents" }, Principal);

        result.Candidates.Datasets.Should().BeEmpty();
    }

    [UnitTest]
    public async Task GroundAsync_LayerCatalogCancellation_IsPropagated()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _layerCatalog.ListLayersAsync(Arg.Any<CancellationToken>())
            .Returns<Task<LayerDefinition[]>>(_ => throw new OperationCanceledException(cts.Token));

        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest { Goal = "incidents" }, Principal, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTest]
    public async Task GroundAsync_AssignsNewIntentIdWhenNoneSupplied()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.DraftIntent.IntentId.Should().StartWith("grounding-");
    }

    [UnitTest]
    public async Task GroundAsync_PreservesCallerSuppliedIntentIdAcrossTurns()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest { Goal = "buffer", IntentId = "intent-existing" },
            Principal);

        result.DraftIntent.IntentId.Should().Be("intent-existing");
    }

    [UnitTest]
    public async Task GroundAsync_RecordsSridAssumptionWhenConstraintsSuppliedWithoutExplicitSrid()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer",
                Constraints = new IntentConstraints { AreaOfInterest = "POLYGON EMPTY" }
            },
            Principal);

        result.DraftIntent.Provenance.Assumptions.Should().Contain(a => a.Contains("srid=4326"));
    }

    [UnitTest]
    public async Task GroundAsync_UnitsOnlyConstraints_DoesNotAddDefaultSridAssumption()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer",
                Constraints = new IntentConstraints { Units = "meters" }
            },
            Principal);

        result.DraftIntent.Provenance.Assumptions.Should().NotContain(a => a.Contains("srid="));
        result.DraftIntent.Provenance.Assumptions.Should().Contain("units=meters");
    }

    [UnitTest]
    public async Task GroundAsync_ClarificationAnswer_RemovesCorrespondingFollowUpQuestion()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("geometry.buffer", 0.9, CandidateKind.Process)]);
        _processCatalog.GetProcess("geometry.buffer").Returns(new ProcessDefinition
        {
            ProcessId = "geometry.buffer",
            Title = "Buffer",
            Description = "Buffers geometries.",
            Category = "geometry",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "distance",
                    DisplayName = "Buffer distance",
                    Description = "meters",
                    ValueType = ProcessParameterValueType.FloatingPoint,
                    Required = true
                }
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "Buffer the roads",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["param.distance"] = ["50"]
                    }
                }
            },
            Principal);

        if (result.Clarification is not null)
        {
            result.Clarification.Questions.Should().NotContain(q => q.QuestionId == "param.distance");
        }

        result.DraftIntent.Provenance.ClarificationsAnswered.Should().Contain("param.distance");
        result.DraftIntent.Provenance.Assumptions.Should().Contain("param.distance=50");
    }

    [UnitTest]
    public async Task GroundAsync_WorkflowFamilyAnswer_OverridesClassifierResult()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.Analyze,
            Confidence = 0.2,
            Evidence = ["fallback:analyze"]
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "Do something vague",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["workflow_family"] = ["PublishData"]
                    }
                }
            },
            Principal);

        result.WorkflowFamily.Value.Should().Be(WorkflowFamily.PublishData);
        result.WorkflowFamily.Confidence.Should().Be(1.0);
        result.WorkflowFamily.Evidence.Should().Contain("clarification");
        result.DraftIntent.WorkflowFamily.Should().Be(WorkflowFamily.PublishData);
        // Engine classification was not consulted because the override short-circuits it.
        _engine.DidNotReceive().Classify(Arg.Any<GroundingRequest>());
    }

    [UnitTest]
    public async Task GroundAsync_WorkflowFamilyAnswer_UnknownValue_Throws()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);

        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["workflow_family"] = ["NotAFamily"]
                    }
                }
            },
            Principal);

        await act.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*workflow_family*");
    }

    [UnitTest]
    public async Task GroundAsync_DatasetSelectionAnswer_PinsChosenCandidateToTop()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreServices(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ServiceCandidate>>())
            .Returns(
            [
                Candidate("svc-a", 0.9, CandidateKind.Dataset),
                Candidate("svc-b", 0.88, CandidateKind.Dataset)
            ]);

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "incidents",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["dataset.selection"] = ["svc-b"]
                    }
                }
            },
            Principal);

        result.Candidates.Datasets.Select(c => c.Id).Should().ContainInOrder("svc-b", "svc-a");
        result.DraftIntent.Provenance.ClarificationsAnswered.Should().Contain("dataset.selection");
    }

    [UnitTest]
    public async Task GroundAsync_ProcessSelectionAnswer_UnknownId_Throws()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("geometry.buffer", 0.9, CandidateKind.Process)]);

        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["process.selection"] = ["geometry.unknown"]
                    }
                }
            },
            Principal);

        await act.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*process.selection*");
    }

    [UnitTest]
    public async Task GroundAsync_PublishTargetAnswer_AppliedToDraftedPublishIntent()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "publish parcels",
                IntentId = "intent-1",
                ExplicitInputs = ["parcels"],
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["publish.target"] = ["TileService"]
                    }
                }
            },
            Principal);

        result.DraftIntent.Publishing.Should().NotBeNull();
        result.DraftIntent.Publishing!.TargetKind.Should().Be(PublishTargetKind.TileService);

        if (result.Clarification is not null)
        {
            result.Clarification.Questions.Should().NotContain(q => q.QuestionId == "publish.target");
        }
    }

    [UnitTest]
    public async Task GroundAsync_PublishTargetAnswer_UnknownValue_Throws()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        });

        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "publish parcels",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["publish.target"] = ["NotATarget"]
                    }
                }
            },
            Principal);

        await act.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*publish.target*");
    }

    [UnitTest]
    public async Task GroundAsync_MergesLayerAndServiceScoresIntoSortedDatasetList()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreLayers(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<LayerCandidate>>())
            .Returns([Candidate("layer-1", 0.6, CandidateKind.Dataset)]);
        _engine.ScoreServices(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ServiceCandidate>>())
            .Returns([Candidate("Service1", 0.8, CandidateKind.Dataset)]);

        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "incidents" }, Principal);

        result.Candidates.Datasets.Select(c => c.Id).Should().ContainInOrder("Service1", "layer-1");
    }

    [UnitTest]
    public async Task GroundAsync_ExposesClarificationEnvelopeWhenAmbiguityDetected()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        });
        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "publish" }, Principal);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.ReasonCodes.Should().Contain(ClarificationReasonCode.PublishAction);
        result.Clarification.IntentId.Should().Be(result.DraftIntent.IntentId);
    }

    [UnitTest]
    public async Task GroundAsync_NoMaterialAmbiguity_ReturnsNullClarification()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer the roads" }, Principal);

        result.Clarification.Should().BeNull();
    }

    private static WorkflowFamilyClassification HighAnalyze => new()
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

    private GroundingService CreateService() => new(
        _engine,
        _processCatalog,
        _authorizationFilter,
        Options.Create(_options),
        NullLogger<GroundingService>.Instance,
        serviceScopeFactory: null,
        _layerCatalog);

    private GroundingService CreateServiceWithoutLayerCatalog() => new(
        _engine,
        _processCatalog,
        _authorizationFilter,
        Options.Create(_options),
        NullLogger<GroundingService>.Instance,
        serviceScopeFactory: null,
        layerCatalog: null);
}
