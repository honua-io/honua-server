// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Grounding.Spec;

[Collection("Database")]
[Protocol(global::Honua.TestKit.Constants.ProtocolNames.Grounding)]
public sealed class SpecGroundingEndpointTests(SpecGroundingEndpointTests.GroundingHost host)
    : IClassFixture<SpecGroundingEndpointTests.GroundingHost>
{
    private readonly HttpClient _client = host.Fixture.Client;

    // One host for the class: each case sends its whole spec and turn, and these endpoints
    // store nothing between requests.
    public sealed class GroundingHost : IAsyncLifetime
    {
        internal WebAppFixture Fixture { get; } = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ReplaceService<IMetadataV2GraphProvider>(SpecGroundingTestSupport.CreateGraphProvider(
                SpecGroundingTestSupport.CreateLayer(1, "Rivers"),
                SpecGroundingTestSupport.CreateLayer(2, "Hospitals North"),
                SpecGroundingTestSupport.CreateLayer(3, "Hospitals South"),
                SpecGroundingTestSupport.CreateLayer(4, "Zones")));

        public Task InitializeAsync() => Fixture.InitializeAsync();

        public Task DisposeAsync() => Fixture.DisposeAsync();
    }

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
    public async Task Mutate_MismatchedClarificationIntent_ReissuesCurrentClarification()
    {
        var firstResponse = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement("{}"),
            turn = "use hospitals as hospitals"
        });

        using var firstPayload = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var expectedIntentId = firstPayload.RootElement
            .GetProperty("clarifications")[0]
            .GetProperty("intent_id")
            .GetString();

        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement("{}"),
            turn = "use hospitals as hospitals",
            clarification_answer = new
            {
                intent_id = "spec-grounding-other",
                answers = new Dictionary<string, string[]>
                {
                    ["dataset.selection"] = ["catalog:layer:3"]
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.TryGetProperty("mutation", out _).Should().BeFalse();
        root.TryGetProperty("error", out _).Should().BeFalse();
        var clarification = root.GetProperty("clarifications")[0];
        clarification.GetProperty("intent_id").GetString().Should().Be(expectedIntentId);
        clarification.GetProperty("kind").GetString().Should().Be("pick-dataset");
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
    public async Task Mutate_MissingTurn_ReturnsProblemDetails()
    {
        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement("{}")
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.GetProperty("title").GetString().Should().Be("Invalid spec grounding request");
        root.GetProperty("detail").GetString().Should().Be("turn is required.");
    }

    [IntegrationTest]
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    public async Task Mutate_CanonicalSpecWithOutOfRangeNumber_ReturnsProblemDetails()
    {
        using var content = new StringContent(
            """
            {
              "spec": {
                "grammar": "v1.0",
                "sources": [
                  {
                    "id": "zones",
                    "type": "layer",
                    "ref": "catalog:layer:4"
                  }
                ],
                "compute": [
                  {
                    "id": "broken",
                    "op": "buffer",
                    "params": {
                      "distance": {
                        "kind": "distance",
                        "unit": "m",
                        "value": 1e400
                      }
                    }
                  }
                ]
              },
              "turn": "use zones"
            }
            """,
            Encoding.UTF8,
            "application/json");
        using var response = await _client.PostAsync(
            "/v1/grounding/spec/mutate",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.GetProperty("title").GetString().Should().Be("Invalid spec grounding request");
        root.GetProperty("detail").GetString().Should().Be("spec could not be read.");
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
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    public async Task Mutate_WithCommunityLicense_ReturnsPaymentRequired()
    {
        var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Community);
        await fixture.InitializeAsync();

        try
        {
            var response = await fixture.Client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
            {
                spec = SpecGroundingTestSupport.ParseJsonElement("{}"),
                turn = "use rivers as rivers"
            });

            response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain(FeatureCatalog.AiGroundingKey);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GroundingSummarize)]
    [Endpoint("POST /v1/grounding/spec/summarize")]
    public async Task Summarize_EmptySpec_ReturnsEmptyAnalysisTitleAndNoSections()
    {
        var response = await _client.PostAsJsonAsync("/v1/grounding/spec/summarize", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement("{}")
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.GetProperty("title_summary").GetString().Should().Be("No sources, computations, or outputs are defined yet.");
        root.GetProperty("section_summaries").GetArrayLength().Should().Be(0);
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

    [IntegrationTest]
    [Operation(Operations.GroundingSummarize)]
    [Endpoint("POST /v1/grounding/spec/summarize")]
    public async Task Summarize_CanonicalSpecWithOutOfRangeNumber_ReturnsProblemDetails()
    {
        using var content = new StringContent(
            """
            {
              "spec": {
                "grammar": "v1.0",
                "sources": [
                  {
                    "id": "zones",
                    "type": "layer",
                    "ref": "catalog:layer:4"
                  }
                ],
                "compute": [
                  {
                    "id": "broken",
                    "op": "buffer",
                    "params": {
                      "distance": {
                        "kind": "distance",
                        "unit": "m",
                        "value": 1e400
                      }
                    }
                  }
                ]
              }
            }
            """,
            Encoding.UTF8,
            "application/json");
        using var response = await _client.PostAsync(
            "/v1/grounding/spec/summarize",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        root.GetProperty("title").GetString().Should().Be("Invalid spec grounding request");
        root.GetProperty("detail").GetString().Should().Be("spec could not be read.");
    }

    [IntegrationTheory]
    [Operation(Operations.GroundingMutate)]
    [Operation(Operations.GroundingSummarize)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    [Endpoint("POST /v1/grounding/spec/summarize")]
    [InlineData("mutate", "sources", "{\"a\":1}")]
    [InlineData("summarize", "sources", "\"x\"")]
    [InlineData("summarize", "compute", "{}")]
    [InlineData("mutate", "map", "[]")]
    public async Task WrongTypedSpecSection_ReturnsProblemDetails(
        string endpoint, string section, string value)
    {
        var expectedDetail = section == "map"
            ? $"spec.{section} must be a JSON object."
            : $"spec.{section} must be a JSON array.";
        using var content = new StringContent(
            $$"""{"spec":{"grammar":"v1.0","{{section}}":{{value}}},"turn":"show rivers on the map"}""",
            Encoding.UTF8,
            "application/json");
        using var response = await _client.PostAsync($"/v1/grounding/spec/{endpoint}", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("title").GetString().Should().Be("Invalid spec grounding request");
        payload.RootElement.GetProperty("detail").GetString().Should().Be(expectedDetail);
    }

    [IntegrationTest]
    [Operation(Operations.GroundingSummarize)]
    [Endpoint("POST /v1/grounding/spec/summarize")]
    public async Task Summarize_NullSpecSections_TreatsThemAsAbsent()
    {
        using var content = new StringContent(
            """{"spec":{"grammar":"v1.0","sources":null,"map":null}}""",
            Encoding.UTF8,
            "application/json");
        using var response = await _client.PostAsync("/v1/grounding/spec/summarize", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [Operation(Operations.GroundingMutate)]
    [Operation(Operations.GroundingSummarize)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    [Endpoint("POST /v1/grounding/spec/summarize")]
    [InlineData("mutate", "{not json")]
    [InlineData("mutate", "[]")]
    [InlineData("summarize", "{not json")]
    [InlineData("summarize", "\"spec\"")]
    public async Task MalformedOrNonObjectBody_Returns400WithoutGrounding(string endpoint, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"/v1/grounding/spec/{endpoint}", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("next_spec");
        text.Should().NotContain("section_summaries");
        text.Should().NotContain("   at ", "a refused body must not leak a stack trace");
    }

    [IntegrationTest]
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    [SecurityTest]
    public async Task Mutate_TurnOneCharacterOverLengthCap_Returns400WithoutGrounding()
    {
        var maxTurnLength = global::Honua.Ai.Grounding.Spec.SpecGroundingEndpointExtensions.MaxTurnLength;
        using var response = await _client.PostAsJsonAsync("/v1/grounding/spec/mutate", new
        {
            spec = SpecGroundingTestSupport.ParseJsonElement("{}"),
            turn = new string('x', maxTurnLength + 1)
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("title").GetString().Should().Be("Invalid spec grounding request");
        payload.RootElement.GetProperty("detail").GetString().Should()
            .Be($"turn must be at most {maxTurnLength} characters.");
        payload.RootElement.TryGetProperty("mutation", out _).Should().BeFalse();
    }

    // A turn at the length cap made of long whitespace runs between clause words must be
    // answered within the time bound.
    [IntegrationTheory]
    [Operation(Operations.GroundingMutate)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    [InlineData("use", "", "!")]
    [InlineData("a", "within 1 of b", "!")]
    [InlineData("show", "on the", "x")]
    public async Task Mutate_WhitespacePaddedTurnAtCap_AnswersWithinBoundedTime(
        string head, string middle, string tail)
    {
        var maxTurnLength = global::Honua.Ai.Grounding.Spec.SpecGroundingEndpointExtensions.MaxTurnLength;
        var padding = new string(' ', (maxTurnLength - head.Length - middle.Length - tail.Length) / 2);
        var turn = head + padding + middle + padding + tail;
        turn.Length.Should().BeLessThanOrEqualTo(maxTurnLength);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var response = await _client.PostAsJsonAsync(
            "/v1/grounding/spec/mutate",
            new { spec = SpecGroundingTestSupport.ParseJsonElement("{}"), turn },
            timeout.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    private static int CountSentences(string text)
        => text.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
}
