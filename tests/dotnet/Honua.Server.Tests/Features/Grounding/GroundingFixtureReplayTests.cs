// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Ai.Grounding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Grounding;

/// <summary>
/// Replays the scenarios in <c>tests/fixtures/grounding/grounding-fixtures.json</c>
/// through the real <see cref="GroundingService"/> + <see cref="DeterministicGroundingEngine"/>
/// + <see cref="BuiltInProcessCatalog"/> stack. These are the same fixtures the
/// honua-server-734 eval harness replays once it lands, so a change to any
/// piece of the pipeline needs to update the JSON in lock-step.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class GroundingFixtureReplayTests
{
    public static TheoryData<GroundingFixtureCase> Fixtures => LoadFixtures();

    [Theory]
    [MemberData(nameof(Fixtures))]
    [Trait("Category", "Conformance")]
    public async Task Replay_ScenarioMatchesExpectedOutput(GroundingFixtureCase fixtureCase)
    {
        var service = BuildService();
        var request = fixtureCase.BuildRequest();

        var result = await service.GroundAsync(request, BuildPrincipal(), CancellationToken.None);

        result.Engine.Should().Be("deterministic");
        result.WorkflowFamily.Value.ToString().Should().Be(fixtureCase.Expected.WorkflowFamily);

        if (fixtureCase.Expected.ClassificationConfidenceBand is { } band)
        {
            BandFor(result.WorkflowFamily.Confidence).Should().Be(band);
        }

        foreach (var keyword in fixtureCase.Expected.ClassificationEvidenceContains ?? [])
        {
            result.WorkflowFamily.Evidence.Should()
                .Contain(e => e.Contains(keyword, StringComparison.Ordinal));
        }

        if (fixtureCase.Expected.DraftIntent is { } expectedDraft)
        {
            switch (expectedDraft.Kind)
            {
                case "Analysis":
                    result.DraftIntent.Analysis.Should().NotBeNull();
                    result.DraftIntent.Publishing.Should().BeNull();
                    break;
                case "Publishing":
                    result.DraftIntent.Publishing.Should().NotBeNull();
                    result.DraftIntent.Analysis.Should().BeNull();
                    result.DraftIntent.Publishing!.SourceKind.ToString()
                        .Should().Be(expectedDraft.PublishSourceKind);
                    result.DraftIntent.Publishing.TargetKind.ToString()
                        .Should().Be(expectedDraft.PublishTargetKind);
                    result.DraftIntent.Publishing.Status.ToString()
                        .Should().Be(expectedDraft.PublishStatus);
                    break;
                case "EnvelopeStub":
                    result.DraftIntent.Analysis.Should().BeNull();
                    result.DraftIntent.Publishing.Should().BeNull();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown expected draft kind '{expectedDraft.Kind}'.");
            }

            if (expectedDraft.RequestedOutputs is { Count: > 0 } outputs)
            {
                result.DraftIntent.RequestedOutputs
                    .Select(o => o.ToString())
                    .Should().Contain(outputs);
            }
        }

        if (fixtureCase.Expected.Candidates?.TopProcessIdContains is { } idSubstring)
        {
            result.Candidates.Processes.Should().NotBeEmpty();
            result.Candidates.Processes[0].Id.Should().Contain(idSubstring);
        }

        if (fixtureCase.Expected.ClarificationPresent is true)
        {
            result.Clarification.Should().NotBeNull();
            foreach (var reason in fixtureCase.Expected.ClarificationReasonCodes ?? [])
            {
                result.Clarification!.ReasonCodes.Select(r => r.ToString())
                    .Should().Contain(reason);
            }
        }
        else if (fixtureCase.Expected.ClarificationPresent is false)
        {
            result.Clarification.Should().BeNull();
        }
    }

    private static GroundingService BuildService()
    {
        var engine = new DeterministicGroundingEngine();
        var catalog = new BuiltInProcessCatalog();
        var authFilter = Substitute.For<IGroundingAuthorizationFilter>();
        authFilter
            .FilterAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<GroundingCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<IReadOnlyList<GroundingCandidate>>(1)));

        var options = Options.Create(new GroundingOptions());
        return new GroundingService(
            engine,
            catalog,
            authFilter,
            options,
            NullLogger<GroundingService>.Instance,
            serviceScopeFactory: null,
            metadataGraphProvider: new EmptyMetadataV2GraphProvider());
    }

    private static ClaimsPrincipal BuildPrincipal()
    {
        var identity = new ClaimsIdentity(authenticationType: "fixture");
        return new ClaimsPrincipal(identity);
    }

    private static string BandFor(double confidence) => confidence switch
    {
        < 0.4 => "Low",
        < 0.8 => "Medium",
        _ => "High"
    };

    private sealed class EmptyMetadataV2GraphProvider : Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider
    {
        private static readonly MetadataV2GraphSnapshot Snapshot = new(
            new MetadataV2Graph(),
            "\"test\"",
            DateTimeOffset.UtcNow);

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => new(Snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => new((MetadataV2GraphSnapshot?)null);
    }

    private static TheoryData<GroundingFixtureCase> LoadFixtures()
    {
        var fixturePath = ResolveFixturePath();
        using var stream = File.OpenRead(fixturePath);
        using var document = JsonDocument.Parse(stream);

        var data = new TheoryData<GroundingFixtureCase>();
        foreach (var scenario in document.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            data.Add(GroundingFixtureCase.FromJson(scenario));
        }

        return data;
    }

    private static string ResolveFixturePath()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "tests", "fixtures", "grounding", "grounding-fixtures.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException(
            "Could not locate tests/fixtures/grounding/grounding-fixtures.json from test base directory.");
    }
}

public sealed record GroundingFixtureCase
{
    public required string Id { get; init; }
    public required string Goal { get; init; }
    public string? WorkflowFamilyHint { get; init; }
    public string? AssumptionPolicy { get; init; }
    public IReadOnlyList<string>? ExplicitInputs { get; init; }
    public required GroundingFixtureExpectation Expected { get; init; }

    public GroundingRequest BuildRequest()
    {
        return new GroundingRequest
        {
            Goal = Goal,
            WorkflowFamilyHint = WorkflowFamilyHint is null
                ? null
                : Enum.Parse<WorkflowFamily>(WorkflowFamilyHint),
            AssumptionPolicy = AssumptionPolicy is null
                ? Honua.Core.Features.Geoprocessing.Domain.AssumptionPolicy.AskWhenMaterial
                : Enum.Parse<AssumptionPolicy>(AssumptionPolicy),
            ExplicitInputs = ExplicitInputs ?? []
        };
    }

    public override string ToString() => Id;

    internal static GroundingFixtureCase FromJson(JsonElement scenario)
    {
        var input = scenario.GetProperty("input");
        var expected = scenario.GetProperty("expected");

        return new GroundingFixtureCase
        {
            Id = scenario.GetProperty("id").GetString()!,
            Goal = input.GetProperty("goal").GetString()!,
            WorkflowFamilyHint = input.TryGetProperty("workflowFamilyHint", out var hint) ? hint.GetString() : null,
            AssumptionPolicy = input.TryGetProperty("assumptionPolicy", out var policy) ? policy.GetString() : null,
            ExplicitInputs = input.TryGetProperty("explicitInputs", out var inputs)
                ? inputs.EnumerateArray().Select(e => e.GetString()!).ToArray()
                : null,
            Expected = GroundingFixtureExpectation.FromJson(expected)
        };
    }
}

public sealed record GroundingFixtureExpectation
{
    public required string WorkflowFamily { get; init; }
    public string? ClassificationConfidenceBand { get; init; }
    public IReadOnlyList<string>? ClassificationEvidenceContains { get; init; }
    public GroundingFixtureDraftExpectation? DraftIntent { get; init; }
    public GroundingFixtureCandidateExpectation? Candidates { get; init; }
    public bool? ClarificationPresent { get; init; }
    public IReadOnlyList<string>? ClarificationReasonCodes { get; init; }

    internal static GroundingFixtureExpectation FromJson(JsonElement element) => new()
    {
        WorkflowFamily = element.GetProperty("workflowFamily").GetString()!,
        ClassificationConfidenceBand = element.TryGetProperty("classificationConfidenceBand", out var band)
            ? band.GetString()
            : null,
        ClassificationEvidenceContains = element.TryGetProperty("classificationEvidenceContains", out var evidence)
            ? evidence.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : null,
        DraftIntent = element.TryGetProperty("draftIntent", out var draft)
            ? GroundingFixtureDraftExpectation.FromJson(draft)
            : null,
        Candidates = element.TryGetProperty("candidates", out var candidates)
            ? GroundingFixtureCandidateExpectation.FromJson(candidates)
            : null,
        ClarificationPresent = element.TryGetProperty("clarificationPresent", out var present)
            ? present.GetBoolean()
            : null,
        ClarificationReasonCodes = element.TryGetProperty("clarificationReasonCodes", out var reasons)
            ? reasons.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : null
    };
}

public sealed record GroundingFixtureDraftExpectation
{
    public required string Kind { get; init; }
    public IReadOnlyList<string>? RequestedOutputs { get; init; }
    public string? PublishSourceKind { get; init; }
    public string? PublishTargetKind { get; init; }
    public string? PublishStatus { get; init; }

    internal static GroundingFixtureDraftExpectation FromJson(JsonElement element) => new()
    {
        Kind = element.GetProperty("kind").GetString()!,
        RequestedOutputs = element.TryGetProperty("requestedOutputs", out var outputs)
            ? outputs.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : null,
        PublishSourceKind = element.TryGetProperty("publishSourceKind", out var src) ? src.GetString() : null,
        PublishTargetKind = element.TryGetProperty("publishTargetKind", out var tgt) ? tgt.GetString() : null,
        PublishStatus = element.TryGetProperty("publishStatus", out var status) ? status.GetString() : null
    };
}

public sealed record GroundingFixtureCandidateExpectation
{
    public string? TopProcessIdContains { get; init; }

    internal static GroundingFixtureCandidateExpectation FromJson(JsonElement element) => new()
    {
        TopProcessIdContains = element.TryGetProperty("topProcessIdContains", out var id) ? id.GetString() : null
    };
}
