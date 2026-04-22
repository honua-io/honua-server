// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Grounding.Spec;

[Collection("Database")]
[Protocol(Protocols.Grounding)]
public sealed class SpecGroundingEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public SpecGroundingEndpointTests()
    {
        _fixture = new WebAppFixture()
            .ReplaceService<Honua.Core.Features.Catalog.Abstractions.ILayerCatalog>(
                new SpecGroundingLayerCatalog(
                    SpecGroundingTestSupport.CreateLayer(1, "Rivers"),
                    SpecGroundingTestSupport.CreateLayer(2, "Hospitals North"),
                    SpecGroundingTestSupport.CreateLayer(3, "Hospitals South"),
                    SpecGroundingTestSupport.CreateLayer(4, "Zones")));
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    public async Task Mutate_EmptySpecUseDataset_ReturnsValidatedMutationPlan()
    {
        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement("{}"),
            turn = "use rivers as rivers"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.GetProperty("clarifications").GetArrayLength().Should().Be(0);
        root.TryGetProperty("error", out _).Should().BeFalse();

        var mutation = root.GetProperty("mutation");
        mutation.GetProperty("mutations")[0].GetProperty("kind").GetString().Should().Be("add-source");
        mutation.GetProperty("sections_touched").EnumerateArray().Select(value => value.GetString())
            .Should().Contain("sources");

        var nextSpec = mutation.GetProperty("next_spec");
        nextSpec.GetProperty("sources")[0].GetProperty("id").GetString().Should().Be("river");
        nextSpec.GetProperty("sources")[0].GetProperty("ref").GetString().Should().Be("catalog:layer:1");
        root.GetProperty("warnings").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    public async Task Mutate_AmbiguousDataset_ReturnsStructuredClarificationEnvelope()
    {
        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement("{}"),
            turn = "use hospitals as hospitals"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.TryGetProperty("mutation", out _).Should().BeFalse();
        root.TryGetProperty("error", out _).Should().BeFalse();

        var clarification = root.GetProperty("clarifications")[0];
        clarification.GetProperty("intent_id").GetString().Should().NotBeNullOrWhiteSpace();
        clarification.GetProperty("kind").GetString().Should().Be("pick-dataset");
        clarification.GetProperty("reason_codes").EnumerateArray().Select(value => value.GetString())
            .Should().Contain("ambiguous_dataset");
        clarification.GetProperty("question_kind").GetString().Should().Be("single-select");
        clarification.GetProperty("candidates").GetArrayLength().Should().Be(2);
        clarification.GetProperty("candidates")[0].GetProperty("candidate_type").GetString().Should().Be("dataset");
    }

    [IntegrationTest]
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    public async Task Mutate_InvalidMutation_ReturnsStructuredErrorWithoutErrorDiagnosticsInWarnings()
    {
        using var harness = new SpecGroundingHarness(SpecGroundingTestSupport.CreateLayer(4, "Zones"));
        var currentSpec = harness.ToCanonicalJson(harness.Parse(
            """
            grammar "v1.0"
            source zones { type = "layer", ref = "catalog:layer:4" }
            """));

        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement(currentSpec),
            turn = "buffer zones by 500.m in EPSG:3857 as zones"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.TryGetProperty("mutation", out _).Should().BeFalse();
        root.GetProperty("clarifications").GetArrayLength().Should().Be(0);
        root.GetProperty("error").GetProperty("kind").GetString().Should().Be("invalid_mutation");
        root.GetProperty("warnings").EnumerateArray()
            .Select(warning => warning.GetProperty("severity").GetString())
            .Should().NotContain("error");
    }

    [IntegrationTest]
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    public async Task Mutate_MissingUnitClarification_ExposesNauticalMilesCandidate()
    {
        using var harness = new SpecGroundingHarness(SpecGroundingTestSupport.CreateLayer(4, "Zones"));
        var currentSpec = harness.ToCanonicalJson(harness.Parse(
            """
            grammar "v1.0"
            source zones { type = "layer", ref = "catalog:layer:4" }
            """));

        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement(currentSpec),
            turn = "buffer zones by 500 in EPSG:3857 as zone_buffer"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var clarification = payload.RootElement.GetProperty("clarifications")[0];
        clarification.GetProperty("kind").GetString().Should().Be("specify-unit");
        clarification.GetProperty("candidates").EnumerateArray()
            .Select(candidate => candidate.GetProperty("unit").GetString())
            .Should().Equal("km", "m", "mi", "ft", "nm");
    }

    [IntegrationTest]
    [Operation(Operations.GroundingSummarize)]
    [Endpoint("POST /v1/grounding/spec/summarize")]
    public async Task Summarize_CanonicalSpec_ReturnsSectionSummariesWithShortText()
    {
        using var harness = new SpecGroundingHarness(SpecGroundingTestSupport.CreateLayer(1, "Rivers"));
        var canonicalJson = harness.ToCanonicalJson(harness.Parse(
            """
            grammar "v1.0"
            source rivers { type = "layer", ref = "catalog:layer:1" }
            compute river_buffer {
              op = buffer
              inputs = { input = @rivers }
              params = { distance = 500.m, crs = "EPSG:3857" }
            }
            output river_buffer_out { expr = @river_buffer }
            """));

        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/summarize", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement(canonicalJson)
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.GetProperty("title_summary").GetString().Should().NotBeNullOrWhiteSpace();

        var sections = root.GetProperty("section_summaries").EnumerateArray().ToArray();
        sections.Should().NotBeEmpty();
        sections.Select(section => section.GetProperty("section_id").GetString())
            .Should().Contain(["sources", "compute", "outputs"]);
        sections.Should().OnlyContain(section =>
            CountSentences(section.GetProperty("text").GetString() ?? string.Empty) <= 2);
    }

    private static int CountSentences(string text)
        => text.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
}
