// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Grounding;
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
/// clarification envelope wiring, layer catalog outage handling (unregistered
/// catalog falls back to empty datasets; injected catalog failures surface
/// <see cref="GroundingErrorKind.CatalogUnavailable"/>), and error translation.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class GroundingServiceTests
{
    private readonly IGroundingEngine _engine = Substitute.For<IGroundingEngine>();
    private readonly IProcessCatalog _processCatalog = Substitute.For<IProcessCatalog>();
    private readonly IGroundingAuthorizationFilter _authorizationFilter = Substitute.For<IGroundingAuthorizationFilter>();
    private readonly IMetadataV2GraphProvider _metadataGraphProvider;
    private readonly GroundingOptions _options = new();
    private MetadataV2GraphSnapshot _metadataSnapshot = CreateSnapshot();
    private Exception? _metadataGraphException;

    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "op")], "Test"));

    public GroundingServiceTests()
    {
        _metadataGraphProvider = new DelegatingMetadataV2GraphProvider(GetSnapshotAsync);
        _engine.Name.Returns("test-engine");
        _processCatalog.ListProcesses().Returns([]);
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
    public async Task GroundAsync_AuthorizationRunsBeforeShortlistCap_SoDeniedTopHitsDoNotHideAllowedCandidates()
    {
        // Regression: ApplyBandsAndCap must run after the authorization filter,
        // or denied top hits consume the MaxCandidatesPerKind budget and hide
        // an authorized candidate ranked just below the cap.
        _options.MaxCandidatesPerKind = 2;
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns(
            [
                Candidate("denied-1", 0.95, CandidateKind.Process),
                Candidate("denied-2", 0.90, CandidateKind.Process),
                Candidate("allowed-below-cap", 0.80, CandidateKind.Process)
            ]);
        _authorizationFilter.Filter(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<GroundingCandidate>>())
            .Returns(callInfo =>
                callInfo.Arg<IReadOnlyList<GroundingCandidate>>()
                    .Where(c => !c.Id.StartsWith("denied", StringComparison.Ordinal))
                    .ToList());

        var service = CreateService();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.Candidates.Processes.Should().ContainSingle()
            .Which.Id.Should().Be("allowed-below-cap");
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
    public async Task GroundAsync_SridConstraintDoesNotSuppressDirectionalProjectionSridClarifications()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("geometry.project", 0.9, CandidateKind.Process)]);
        _processCatalog.GetProcess("geometry.project").Returns(new ProcessDefinition
        {
            ProcessId = "geometry.project",
            Title = "Project",
            Description = "Reprojects geometries from one spatial reference to another.",
            Category = "geometry",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "fromSrid",
                    DisplayName = "From SRID",
                    Description = "Source SRID",
                    ValueType = ProcessParameterValueType.Srid,
                    Required = true
                },
                new ProcessParameterSpec
                {
                    Name = "toSrid",
                    DisplayName = "To SRID",
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

        result.Clarification.Should().NotBeNull();
        result.Clarification!.ReasonCodes.Should().Contain(ClarificationReasonCode.MissingRequiredInput);
        result.Clarification.Questions.Should().Contain(q => q.QuestionId == "param.fromSrid");
        result.Clarification.Questions.Should().Contain(q => q.QuestionId == "param.toSrid");
    }

    [UnitTest]
    public async Task GroundAsync_MissingMetadataGraphProvider_ReturnsEmptyDatasetsAndNoFailure()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);

        var service = CreateServiceWithoutMetadataGraphProvider();

        var result = await service.GroundAsync(new GroundingRequest { Goal = "buffer" }, Principal);

        result.Candidates.Datasets.Should().BeEmpty();
    }

    [UnitTest]
    public async Task GroundAsync_MetadataGraphProviderThrows_ThrowsCatalogUnavailable()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _metadataGraphException = new InvalidOperationException("catalog down");

        var service = CreateService();

        var act = async () => await service.GroundAsync(new GroundingRequest { Goal = "incidents" }, Principal);

        var ex = (await act.Should().ThrowAsync<GroundingException>()).Which;
        ex.Kind.Should().Be(GroundingErrorKind.CatalogUnavailable);
        ex.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [UnitTest]
    public async Task GroundAsync_MetadataGraphProviderServicesUnavailable_ThrowsCatalogUnavailable()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _metadataGraphException = new InvalidOperationException("services unavailable");

        var service = CreateService();

        var act = async () => await service.GroundAsync(new GroundingRequest { Goal = "incidents" }, Principal);

        var ex = (await act.Should().ThrowAsync<GroundingException>()).Which;
        ex.Kind.Should().Be(GroundingErrorKind.CatalogUnavailable);
        ex.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [UnitTest]
    public async Task GroundAsync_MetadataGraphProviderCancellation_IsPropagated()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _metadataGraphException = new OperationCanceledException(cts.Token);

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
    public async Task GroundAsync_WhitespaceOnlyIntentId_AssignsNewIntentId()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest { Goal = "buffer", IntentId = "   " },
            Principal);

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
    public async Task GroundAsync_MultipleParameterAnswers_EmitsDeterministicAssumptionOrder()
    {
        // Regression: two semantically identical clarification payloads whose
        // param.* answers differ only in key insertion order must produce the
        // same `provenance.assumptions` sequence, preserving the grounding
        // slice's documented stability guarantee.
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([Candidate("geometry.project", 0.9, CandidateKind.Process)]);
        _processCatalog.GetProcess("geometry.project").Returns(new ProcessDefinition
        {
            ProcessId = "geometry.project",
            Title = "Project",
            Description = "Reprojects geometries from one spatial reference to another.",
            Category = "geometry",
            Parameters =
            [
                new ProcessParameterSpec
                {
                    Name = "fromSrid",
                    DisplayName = "From SRID",
                    Description = "Source SRID",
                    ValueType = ProcessParameterValueType.Srid,
                    Required = true
                },
                new ProcessParameterSpec
                {
                    Name = "toSrid",
                    DisplayName = "To SRID",
                    Description = "Target SRID",
                    ValueType = ProcessParameterValueType.Srid,
                    Required = true
                }
            ],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        });

        var service = CreateService();

        var forwardOrder = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "project the geometries",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["param.fromSrid"] = ["4326"],
                        ["param.toSrid"] = ["3857"]
                    }
                }
            },
            Principal);

        var reverseOrder = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "project the geometries",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["param.toSrid"] = ["3857"],
                        ["param.fromSrid"] = ["4326"]
                    }
                }
            },
            Principal);

        forwardOrder.DraftIntent.Provenance.Assumptions
            .Should().Equal(reverseOrder.DraftIntent.Provenance.Assumptions,
                "param.* assumptions must be ordered independently of caller key order");
        forwardOrder.DraftIntent.Provenance.Assumptions.Should().ContainInOrder(
            "param.fromSrid=4326", "param.toSrid=3857");
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
    public async Task GroundAsync_ProcessSelectionAnswer_EmptyRefreshedRanking_Throws()
    {
        // Regression: if the refreshed ranking is empty (for example because
        // the caller changed the goal and no process now matches), a pinned
        // process id must not be silently ignored — the docs advertise an
        // invalid_argument error on stale pins.
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns([]);

        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "xylophone quartz zebra",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["process.selection"] = ["geometry.buffer"]
                    }
                }
            },
            Principal);

        await act.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage("*process.selection*");
    }

    [UnitTest]
    public async Task GroundAsync_ProcessSelectionAnswer_PinnedCandidateBelowShortlistCap_PromotedNotRejected()
    {
        // Regression: pin application must run against the post-authorization
        // ranking, not the post-cap ranking. A still-authorized candidate
        // that has fallen just below MaxCandidatesPerKind must be promoted
        // to the front rather than rejected as invalid_argument because the
        // shortlist trim hid it from ApplyPin. Contract is documented in
        // docs/developer/GROUNDING.md (`process.selection` reorders the
        // post-authorization ranking).
        _options.MaxCandidatesPerKind = 2;
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ProcessDefinition>>())
            .Returns(
            [
                Candidate("p-top", 0.95, CandidateKind.Process),
                Candidate("p-second", 0.90, CandidateKind.Process),
                Candidate("p-below-cap", 0.80, CandidateKind.Process)
            ]);

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["process.selection"] = ["p-below-cap"]
                    }
                }
            },
            Principal);

        result.Candidates.Processes.Should().HaveCount(2);
        result.Candidates.Processes.Select(c => c.Id).Should().ContainInOrder("p-below-cap", "p-top");
        result.DraftIntent.Provenance.ClarificationsAnswered.Should().Contain("process.selection");
    }

    [UnitTest]
    public async Task GroundAsync_DatasetSelectionAnswer_PinnedCandidateBelowShortlistCap_PromotedNotRejected()
    {
        // Regression for the dataset side of the same finding: a pinned
        // dataset that is still authorized but ranks below the shortlist
        // cap must be promoted instead of trimmed-then-rejected.
        _options.MaxCandidatesPerKind = 2;
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        _engine.ScoreServices(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ServiceCandidate>>())
            .Returns(
            [
                Candidate("svc-top", 0.95, CandidateKind.Dataset),
                Candidate("svc-second", 0.90, CandidateKind.Dataset),
                Candidate("svc-below-cap", 0.80, CandidateKind.Dataset)
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
                        ["dataset.selection"] = ["svc-below-cap"]
                    }
                }
            },
            Principal);

        result.Candidates.Datasets.Should().HaveCount(2);
        result.Candidates.Datasets.Select(c => c.Id).Should().ContainInOrder("svc-below-cap", "svc-top");
        result.DraftIntent.Provenance.ClarificationsAnswered.Should().Contain("dataset.selection");
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
    public async Task GroundAsync_PublishWithoutSource_AnsweringOnlyPublishTarget_StillRequestsSource()
    {
        // Regression for Finding #2 (codex_local): previously, answering
        // publish.target without any explicit inputs or high-confidence
        // datasets terminated with draftIntent.publishing == null AND a null
        // clarification — the caller could not escape. With the publish.source
        // finding, the follow-up pass keeps asking for the source until it is
        // pinned.
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "publish my data",
                IntentId = "intent-1",
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

        result.DraftIntent.Publishing.Should().BeNull("no source was supplied yet");
        result.Clarification.Should().NotBeNull(
            "the publish flow cannot terminate silently when the source is still unresolved");
        result.Clarification!.Questions.Should().Contain(q => q.QuestionId == "publish.source");
    }

    [UnitTest]
    public async Task GroundAsync_PublishSourceAnswer_ProducesDraftPublishingWithResolvedSource()
    {
        // publish.source answers must flow into the drafted PublishIntent.SourceId
        // so the caller can complete the publish flow without ever setting
        // ExplicitInputs.
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        });

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "publish my data",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["publish.source"] = ["parcels-layer"],
                        ["publish.target"] = ["TileService"]
                    }
                }
            },
            Principal);

        result.DraftIntent.Publishing.Should().NotBeNull();
        result.DraftIntent.Publishing!.SourceId.Should().Be("parcels-layer");
        result.DraftIntent.Publishing.TargetKind.Should().Be(PublishTargetKind.TileService);
        result.DraftIntent.Provenance.ClarificationsAnswered.Should().Contain("publish.source");

        if (result.Clarification is not null)
        {
            result.Clarification.Questions.Should().NotContain(q => q.QuestionId == "publish.source");
            result.Clarification.Questions.Should().NotContain(q => q.QuestionId == "publish.target");
        }
    }

    [UnitTest]
    public async Task GroundAsync_PublishSourceAnswer_ServiceCandidateId_StillRequestsSource()
    {
        // Regression: a `publish.source` answer that names a service
        // catalog id cannot be drafted — PublishSourceKind has no
        // FeatureService value. Instead of producing a publish intent
        // mislabeled as FeatureLayer, the service must drop the answer,
        // omit the `publishing` block, and re-emit `publish.source` so the
        // caller picks a publishable layer-backed source.
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        });
        _engine.ScoreServices(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ServiceCandidate>>())
            .Returns(
            [
                new GroundingCandidate
                {
                    Id = "Parcels",
                    Kind = CandidateKind.Dataset,
                    DatasetSubtype = DatasetSubtype.Service,
                    DisplayName = "Parcels",
                    Score = 0.95,
                    ConfidenceBand = ConfidenceBand.High
                }
            ]);

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "publish parcels",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["publish.source"] = ["Parcels"],
                        ["publish.target"] = ["FeatureService"]
                    }
                }
            },
            Principal);

        result.DraftIntent.Publishing.Should().BeNull(
            "a service catalog id cannot be drafted as PublishSourceKind.FeatureLayer");
        result.Clarification.Should().NotBeNull(
            "the publish flow cannot terminate silently when the answer was a service id");
        result.Clarification!.Questions.Should().Contain(q => q.QuestionId == "publish.source");
        result.DraftIntent.Provenance.ClarificationsAnswered.Should().NotContain("publish.source");
    }

    [UnitTest]
    public async Task GroundAsync_PublishExplicitInputs_ServiceCandidateId_StillRequestsSource()
    {
        // Regression: an `ExplicitInputs` pin that names a service catalog
        // id must not bypass the `publish.source` clarification — the
        // drafter cannot label a service id as FeatureLayer. The evaluator
        // must keep asking for a source and the drafter must omit the
        // publishing block.
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.PublishData,
            Confidence = 1.0
        });
        _engine.ScoreServices(Arg.Any<GroundingRequest>(), Arg.Any<IReadOnlyList<ServiceCandidate>>())
            .Returns(
            [
                new GroundingCandidate
                {
                    Id = "Parcels",
                    Kind = CandidateKind.Dataset,
                    DatasetSubtype = DatasetSubtype.Service,
                    DisplayName = "Parcels",
                    Score = 0.95,
                    ConfidenceBand = ConfidenceBand.High
                }
            ]);

        var service = CreateService();

        var result = await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "publish parcels",
                IntentId = "intent-1",
                ExplicitInputs = ["Parcels"]
            },
            Principal);

        result.DraftIntent.Publishing.Should().BeNull(
            "a service catalog id in ExplicitInputs cannot be drafted as PublishSourceKind.FeatureLayer");
        result.Clarification.Should().NotBeNull();
        result.Clarification!.Questions.Should().Contain(q => q.QuestionId == "publish.source");
    }

    [UnitTest]
    public async Task GroundAsync_PaddedPublishSourceAnswer_TrimsValueBeforeDrafting()
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
                Goal = "publish my data",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["publish.source"] = ["  parcels-layer  "],
                        ["publish.target"] = [" TileService "]
                    }
                }
            },
            Principal);

        result.DraftIntent.Publishing.Should().NotBeNull();
        result.DraftIntent.Publishing!.SourceId.Should().Be("parcels-layer");
        result.DraftIntent.Publishing.TargetKind.Should().Be(PublishTargetKind.TileService);
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

    [UnitTest]
    public async Task GroundAsync_ClarificationResponse_IntentIdMismatch_ThrowsInvalidArgument()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer the roads",
                IntentId = "intent-request",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-other",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["workflow_family"] = ["Analyze"]
                    }
                }
            },
            Principal);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("intent-other").And.Contain("intent-request");
        _engine.DidNotReceive().Classify(Arg.Any<GroundingRequest>());
    }

    [UnitTest]
    public async Task GroundAsync_ClarificationResponse_MissingRequestIntentId_ThrowsInvalidArgument()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer the roads",
                IntentId = null,
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "intent-1",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["workflow_family"] = ["Analyze"]
                    }
                }
            },
            Principal);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("intentId");
    }

    [UnitTest]
    public async Task GroundAsync_ClarificationResponse_BlankResponseIntentId_ThrowsInvalidArgument()
    {
        _engine.Classify(Arg.Any<GroundingRequest>()).Returns(HighAnalyze);
        var service = CreateService();

        var act = async () => await service.GroundAsync(
            new GroundingRequest
            {
                Goal = "buffer the roads",
                IntentId = "intent-1",
                ClarificationResponse = new ClarificationResponse
                {
                    IntentId = "   ",
                    Answers = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["workflow_family"] = ["Analyze"]
                    }
                }
            },
            Principal);

        (await act.Should().ThrowAsync<GeoprocessingValidationException>())
            .Which.Message.Should().Contain("intentId");
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
        _metadataGraphProvider);

    private GroundingService CreateServiceWithoutMetadataGraphProvider() => new(
        _engine,
        _processCatalog,
        _authorizationFilter,
        Options.Create(_options),
        NullLogger<GroundingService>.Instance,
        serviceScopeFactory: null,
        metadataGraphProvider: null);

    private ValueTask<MetadataV2GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_metadataGraphException is OperationCanceledException canceled)
        {
            throw canceled;
        }

        if (_metadataGraphException is not null)
        {
            throw _metadataGraphException;
        }

        return new ValueTask<MetadataV2GraphSnapshot>(_metadataSnapshot);
    }

    private static MetadataV2GraphSnapshot CreateSnapshot(MetadataV2Graph? graph = null)
        => new(graph ?? new MetadataV2Graph(), "\"test\"", DateTimeOffset.UtcNow);

    private sealed class DelegatingMetadataV2GraphProvider(
        Func<CancellationToken, ValueTask<MetadataV2GraphSnapshot>> getCurrent) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => getCurrent(cancellationToken);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => new((MetadataV2GraphSnapshot?)null);
    }
}
