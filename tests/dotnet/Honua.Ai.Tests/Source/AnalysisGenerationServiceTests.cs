// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.AnalysisGeneration;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.AnalysisGeneration;

/// <summary>
/// Exercises the natural-language analysis-generation vertical end-to-end with a stubbed
/// OpenAI-compatible model: the prompt grounds in the live process catalog, the canned proposal
/// references a real catalog method (e.g. <c>geometry.buffer</c>) and passes the generation-lenient
/// structural gate, and a model that maps no request surfaces <c>unsupported</c> rather than a plan.
/// This is the analysis-family counterpart to <c>WorkflowGenerationServiceTests</c> and locks in the
/// catalog vocabulary the Studio NL→Analysis generation (workflow #2) depends on so it stops
/// returning <c>unsupported</c> for buffer/spatial-filter requests.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class AnalysisGenerationServiceTests
{
    private const string GenerateContract = "POST /api/v1/analysis/content/generate";

    private readonly BuiltInProcessCatalog _catalog = new();

    // -----------------------------------------------------------------------
    // Catalog grounding vocabulary — the methods the Studio NL→Analysis flow
    // must be able to map "buffer parcels by 100m" / "parcels within 500m of
    // rivers" / "clip", "centroid", "dissolve" requests to.
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public void Catalog_AdvertisesTheCommonlyRequestedGeospatialAnalysisMethods()
    {
        // Buffer, spatial-filter, clip, centroid, dissolve, and spatial/distance join are the
        // staples a generation request commonly asks for; each must be a real catalog method so
        // the schema enum + grounding prompt can ground the model on it.
        string[] expected =
        [
            "geometry.buffer",
            "analytics.buffer-aggregate",
            "transform.spatial-filter",
            "geometry.clip",
            "transform.clip",
            "geometry.centroid",
            "geometry.dissolve",
            "generalization.dissolve",
            "analytics.spatial-join",
        ];

        foreach (var processId in expected)
        {
            _catalog.GetProcess(processId).Should().NotBeNull(
                $"the analysis-generation vocabulary must include '{processId}'");
        }
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public void Catalog_Buffer_DeclaresDistanceParameterSoARangeRequestCanBeGrounded()
    {
        var buffer = _catalog.GetProcess("geometry.buffer");

        buffer.Should().NotBeNull();
        buffer!.Parameters.Should().Contain(p => p.Name == "distance" && p.Required);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public void GroundingPrompt_EnumeratesTheBufferAndSpatialFilterMethods()
    {
        var request = new AnalysisGenerationProviderRequest { Prompt = "buffer all parcels by 100 meters" };

        var system = AnalysisGenerationPrompt.BuildSystem(request, _catalog);

        // The grounded method catalog block must advertise the new processes (id + parameters) so a
        // local model can map a buffer/spatial-filter request onto them.
        system.Should().Contain("geometry.buffer");
        system.Should().Contain("transform.spatial-filter");
        system.Should().Contain("geometry.centroid");
        system.Should().Contain("geometry.dissolve");
        // Parameter grounding: the buffer distance parameter is enumerated for the model.
        system.Should().Contain("distance");
    }

    // -----------------------------------------------------------------------
    // Generation-lenient structural gate over the catalog.
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public void Gate_BufferPlanReferencingARealMethod_Passes()
    {
        var gate = AnalysisGenerationValidationGate.Evaluate(BufferPlan(), _catalog);

        gate.Passed.Should().BeTrue();
        gate.StructuralFailures.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public void Gate_PlanReferencingAnUnknownMethod_FailsStructurally()
    {
        var plan = new AnalysisPlan
        {
            PlanId = "p1",
            IntentId = "i1",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "s1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = "carrier.pigeon"
                }
            ]
        };

        var gate = AnalysisGenerationValidationGate.Evaluate(plan, _catalog);

        gate.Passed.Should().BeFalse();
        gate.StructuralFailures.Should().Contain(f => f.Code == "UNKNOWN_PROCESS");
    }

    // -----------------------------------------------------------------------
    // Service end-to-end with a stubbed model.
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public async Task GenerateAsync_BufferRequest_ReturnsGeneratedWithABindingToTheBufferMethod()
    {
        // The exact scenario the Studio NL→Analysis flow previously failed: a "buffer parcels by
        // 100 meters" request that returned status=unsupported. With the catalog grounding the model
        // and the structural gate accepting the real method, the turn now returns a generated plan
        // bound to geometry.buffer.
        var service = CreateService(enabled: true, ProposalJson(BufferProposal()));

        var result = await service.GenerateAsync(new AnalysisGenerationRequest
        {
            Prompt = "buffer all parcels by 100 meters"
        });

        result.Status.Should().Be("generated");
        result.Analysis.Should().NotBeNull();
        result.Analysis!.Plan.Steps.Should().ContainSingle()
            .Which.ProcessId.Should().Be("geometry.buffer");
        result.Validation.Should().NotBeNull();
        result.Validation!.Issues.Should().NotContain(i => i.Severity == "error");
        result.Provider.Should().Be("local");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public async Task GenerateAsync_SpatialFilterRequest_ReturnsGeneratedWithABindingToTheSpatialFilterMethod()
    {
        // "Find parcels within a region" maps to transform.spatial-filter — another staple that
        // previously fell through to unsupported.
        var service = CreateService(enabled: true, ProposalJson(SpatialFilterProposal()));

        var result = await service.GenerateAsync(new AnalysisGenerationRequest
        {
            Prompt = "find parcels within the downtown bounding box"
        });

        result.Status.Should().Be("generated");
        result.Analysis.Should().NotBeNull();
        result.Analysis!.Plan.Steps.Should().ContainSingle()
            .Which.ProcessId.Should().Be("transform.spatial-filter");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public async Task GenerateAsync_ModelMapsNothing_PassesThroughUnsupportedWithUnmappedRequests()
    {
        const string unsupported =
            """
            { "status": "unsupported", "rationale": "No catalog method matches a carrier pigeon dispatch.", "unmappedRequests": ["send a carrier pigeon to the county office"] }
            """;
        var service = CreateService(enabled: true, ProposalJson(unsupported));

        var result = await service.GenerateAsync(new AnalysisGenerationRequest
        {
            Prompt = "send a carrier pigeon to the county office"
        });

        result.Status.Should().Be("unsupported");
        result.Analysis.Should().BeNull();
        result.UnmappedRequests.Should().NotBeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public async Task GenerateAsync_WhenDisabled_ReportsUnsupportedWithoutCallingAProvider()
    {
        var service = CreateService(enabled: false, throwIfCalled: true);

        var result = await service.GenerateAsync(new AnalysisGenerationRequest
        {
            Prompt = "buffer all parcels by 100 meters"
        });

        result.Status.Should().Be("unsupported");
        result.Analysis.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint(GenerateContract)]
    public async Task GenerateAsync_ModelInventsAnUnknownMethod_RepairsThenErrorsRatherThanReturningAnInvalidPlan()
    {
        // The model first proposes a non-catalog method; the bounded repair loop re-prompts and the
        // model keeps proposing the bad method, so the gate converts the structurally-invalid plan
        // into an error result — never a "generated" plan that fails validation.
        var service = CreateService(
            enabled: true,
            ProposalJson(InvalidMethodProposal()),
            ProposalJson(InvalidMethodProposal()));

        var result = await service.GenerateAsync(new AnalysisGenerationRequest
        {
            Prompt = "buffer all parcels by 100 meters"
        });

        result.Status.Should().Be("error");
        result.Analysis.Should().BeNull();
        result.Validation.Should().NotBeNull();
        result.Validation!.Issues.Should().Contain(i => i.Code == "UNKNOWN_PROCESS" && i.Severity == "error");
    }

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    private static AnalysisPlan BufferPlan() => new()
    {
        PlanId = "buffer-plan",
        IntentId = "buffer-intent",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "s1",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "geometry.buffer",
                Inputs = new Dictionary<string, string>
                {
                    ["wkb"] = "AAAA",
                    ["srid"] = "4326",
                    ["distance"] = "100"
                }
            }
        ],
        Outputs = [ArtifactKind.FeatureLayer]
    };

    private static string BufferProposal() =>
        """
        {
          "status": "generated",
          "rationale": "Buffer each parcel geometry by 100 meters and emit a feature layer.",
          "analysis": {
            "intent": { "intentId": "buffer-intent", "goal": "buffer parcels by 100 meters", "mode": "analysis", "inputs": ["parcels"], "requestedOutputs": ["FeatureLayer"] },
            "plan": {
              "planId": "buffer-plan",
              "intentId": "buffer-intent",
              "steps": [
                { "stepId": "s1", "kind": "Geoprocess", "processId": "geometry.buffer", "inputs": { "wkb": "AAAA", "srid": "4326", "distance": "100" }, "dependsOn": [] }
              ],
              "outputs": ["FeatureLayer"],
              "warnings": []
            },
            "requestedArtifacts": ["FeatureLayer"]
          }
        }
        """;

    private static string SpatialFilterProposal() =>
        """
        {
          "status": "generated",
          "rationale": "Keep only parcels whose geometry intersects the requested bounding box.",
          "analysis": {
            "intent": { "intentId": "filter-intent", "goal": "find parcels within the bounding box", "mode": "analysis", "inputs": ["parcels"], "requestedOutputs": ["FeatureLayer"] },
            "plan": {
              "planId": "filter-plan",
              "intentId": "filter-intent",
              "steps": [
                { "stepId": "s1", "kind": "Geoprocess", "processId": "transform.spatial-filter", "inputs": { "input": "data:application/geo+json;base64,e30=", "bbox": "0,0,10,10", "predicate": "intersects" }, "dependsOn": [] }
              ],
              "outputs": ["FeatureLayer"],
              "warnings": []
            },
            "requestedArtifacts": ["FeatureLayer"]
          }
        }
        """;

    private static string InvalidMethodProposal() =>
        """
        {
          "status": "generated",
          "rationale": "Attempting an unsupported method.",
          "analysis": {
            "intent": { "intentId": "bad-intent", "goal": "buffer parcels", "mode": "analysis", "inputs": ["parcels"], "requestedOutputs": ["FeatureLayer"] },
            "plan": {
              "planId": "bad-plan",
              "intentId": "bad-intent",
              "steps": [
                { "stepId": "s1", "kind": "Geoprocess", "processId": "geometry.buffer", "inputs": { "wkb": "AAAA", "srid": "4326", "distance": "100" }, "dependsOn": [] },
                { "stepId": "s2", "kind": "Geoprocess", "processId": "nonexistent.method", "inputs": {}, "dependsOn": ["s1"] }
              ],
              "outputs": ["FeatureLayer"],
              "warnings": []
            },
            "requestedArtifacts": ["FeatureLayer"]
          }
        }
        """;

    // Wrap a model proposal JSON in an OpenAI-compatible chat-completion envelope.
    private static string ProposalJson(string proposalContent)
    {
        var content = JsonSerializer.Serialize(proposalContent);
        return $$"""
            { "id": "chatcmpl-test", "choices": [ { "index": 0, "message": { "role": "assistant", "content": {{content}} }, "finish_reason": "stop" } ] }
            """;
    }

    private AnalysisGenerationService CreateService(bool enabled, params string[] cannedResponses)
        => CreateService(enabled, throwIfCalled: false, cannedResponses);

    private AnalysisGenerationService CreateService(bool enabled, bool throwIfCalled, params string[] cannedResponses)
    {
        var configuration = Options.Create(new WorkflowGenerationConfiguration
        {
            Enabled = enabled,
            DefaultProvider = WorkflowGenerationConfiguration.LocalProviderId,
            MaxRepairAttempts = 1,
            Providers =
            {
                [WorkflowGenerationConfiguration.LocalProviderId] = new WorkflowGenerationProviderOptions
                {
                    Endpoint = "http://localhost:11434/v1",
                    Model = "qwen2.5-coder:3b",
                    TimeoutSeconds = 30,
                    MaxTokens = 4096
                }
            }
        });

        var handler = new StubChatHandler(cannedResponses, throwIfCalled);
        var factory = new StubHttpClientFactory(handler);

        return new AnalysisGenerationService(factory, configuration, _catalog);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Returns the next canned chat-completion body per call so the repair loop can be exercised.
    private sealed class StubChatHandler(IReadOnlyList<string> responses, bool throwIfCalled) : HttpMessageHandler
    {
        private int _call;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throwIfCalled)
            {
                throw new InvalidOperationException("The provider must not be called when generation is disabled.");
            }

            var index = Math.Min(_call, responses.Count - 1);
            _call++;
            var body = responses.Count == 0 ? "{}" : responses[index];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
