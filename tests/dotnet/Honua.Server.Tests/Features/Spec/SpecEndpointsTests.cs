// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Spec;

/// <summary>
/// End-to-end coverage of the <c>/v1/spec/*</c> HTTP surface. Proves the SSE
/// event shape, plan structure, artifact retrieval, and error envelopes — the
/// operator-facing evidence for ticket #789's acceptance criteria.
/// </summary>
[Protocol(TestProtocols.Spec)]
public sealed class SpecEndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/validate")]
    public async Task Validate_TextSpec_ReturnsDiagnosticsAndCanonicalJson()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/spec/validate", JsonContent(new
        {
            text = """
            grammar "v1.0"
            source hospitals { type = "layer", ref = "catalog:layer:1" }
            compute buffered {
              op = buffer
              inputs = { input = @hospitals }
              params = { distance = 500.m, crs = "EPSG:3857" }
            }
            output result { expr = @buffered }
            """,
            includeCanonicalJson = true
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.True(root.GetProperty("isValid").GetBoolean());
        Assert.Equal("v1.0", root.GetProperty("grammar").GetString());
        Assert.Equal("v1.0", root.GetProperty("operatorCapabilityVersion").GetString());

        var canonicalJson = root.GetProperty("canonicalJson").GetString();
        Assert.False(string.IsNullOrWhiteSpace(canonicalJson));
        using var canonical = JsonDocument.Parse(canonicalJson!);
        Assert.Equal("v1.0", canonical.RootElement.GetProperty("grammar").GetString());
        Assert.Equal("buffer", canonical.RootElement.GetProperty("compute")[0].GetProperty("op").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/validate")]
    public async Task Validate_TextSpecWithUnknownOperator_ReturnsInvalidWithDiagnostic()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/spec/validate", JsonContent(new
        {
            text = """
            grammar "v1.0"
            source hospitals { type = "layer", ref = "catalog:layer:1" }
            compute broken {
              op = does_not_exist
              inputs = { input = @hospitals }
            }
            """
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.False(root.GetProperty("isValid").GetBoolean());
        Assert.Contains(root.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == "UnknownOperator" &&
            diagnostic.GetProperty("severity").GetString() == "error");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/validate")]
    public async Task Validate_CanonicalSpecObject_RunsSameValidator()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/spec/validate", JsonContent(new
        {
            spec = new
            {
                grammar = "v1.0",
                sources = new[]
                {
                    new
                    {
                        id = "hospitals",
                        type = "layer",
                        @ref = "catalog:layer:1"
                    }
                },
                compute = new[]
                {
                    new
                    {
                        id = "broken",
                        op = "does_not_exist"
                    }
                }
            }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.False(root.GetProperty("isValid").GetBoolean());
        Assert.Contains(root.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == "UnknownOperator");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/validate")]
    public async Task Validate_CanonicalSpecObjectWithOutOfRangeNumber_ReturnsParseDiagnostic()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/spec/validate",
            new StringContent(
                """
                {
                  "spec": {
                    "grammar": "v1.0",
                    "sources": [
                      {
                        "id": "hospitals",
                        "type": "layer",
                        "ref": "catalog:layer:1"
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
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.False(root.GetProperty("isValid").GetBoolean());
        Assert.Contains(root.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == "ParseError" &&
            diagnostic.GetProperty("severity").GetString() == "error");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/validate")]
    public async Task Validate_WithoutSource_Returns400WithInvalidRequestBody()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/spec/validate",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-request-body", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_LinearChain_ReturnsDagWithContentHashesInTopologicalOrder()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        var nodes = root.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        Assert.Equal("a", nodes[0].GetProperty("nodeId").GetString());
        Assert.Equal("b", nodes[1].GetProperty("nodeId").GetString());

        var hashA = nodes[0].GetProperty("contentHash").GetString()!;
        var hashB = nodes[1].GetProperty("contentHash").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", hashA);
        Assert.Matches("^[0-9a-f]{64}$", hashB);
        Assert.NotEqual(hashA, hashB);

        Assert.Equal("a", nodes[1].GetProperty("dependsOn")[0].GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_MalformedJson_Returns400WithInvalidRequestBody()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/spec/plan",
            new StringContent("{not:valid", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-request-body", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_MissingGrammarVersion_Returns400WithVersionSkew()
    {
        // Blank grammar/process-family versions collide in the content-hash
        // cache key. The planner rejects at the entry point so REST maps to
        // 400 / `version-skew` before the executor is reached.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "",
            ["processFamilyVersion"] = "family/1.0",
            ["nodes"] = new[] { ComputeNode("a") }
        };

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("version-skew", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_MissingProcessFamilyVersion_Returns400WithoutOpeningStream()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "grammar/1.0",
            ["processFamilyVersion"] = "",
            ["nodes"] = new[] { ComputeNode("a") }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("version-skew", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_NodeWithoutKind_Returns400WithUnknownKind()
    {
        // Missing 'kind' must not silently coerce to Compute — operators need an
        // explicit rejection so a typo or omission does not accidentally
        // dispatch through the compute executor.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var nodeWithoutKind = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["op"] = "compute.noop"
        };
        var document = BuildDocument(nodeWithoutKind);

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown-kind", payload.RootElement.GetProperty("code").GetString());
        Assert.Equal("a", payload.RootElement.GetProperty("nodeId").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_NodeWithoutKind_Returns400WithoutOpeningStream()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var nodeWithoutKind = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["op"] = "compute.noop"
        };
        var document = BuildDocument(nodeWithoutKind);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown-kind", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_NumericKindOutOfRange_Returns400WithUnknownKind()
    {
        // JsonStringEnumConverter still honours numeric enum values by default,
        // so a client sending "kind": 999 would otherwise produce
        // (SpecResourceKind)999 that passes the null check and flows through to
        // the orchestrator. The transport-boundary whitelist must reject it
        // with the same `unknown-kind` code operators already handle.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var numericKindNode = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["kind"] = 999,
            ["op"] = "compute.noop"
        };
        var document = BuildDocument(numericKindNode);

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown-kind", payload.RootElement.GetProperty("code").GetString());
        Assert.Equal("a", payload.RootElement.GetProperty("nodeId").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_NumericKindOutOfRange_Returns400WithoutOpeningStream()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var numericKindNode = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["kind"] = 999,
            ["op"] = "compute.noop"
        };
        var document = BuildDocument(numericKindNode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown-kind", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_NumericCacheModeOutOfRange_Returns400WithoutOpeningStream()
    {
        // JsonStringEnumConverter still accepts numeric enum values, so a
        // client sending "cacheMode": 999 would otherwise land in the gRPC
        // mapping's catch-all arm and silently flow through as ReadWrite. The
        // transport boundary must reject with the stable `unknown-cache-mode`
        // code before the apply starts.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "grammar/1.0",
            ["processFamilyVersion"] = "family/1.0",
            ["nodes"] = new[] { ComputeNode("a") },
            ["cacheMode"] = 999
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown-cache-mode", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_Cycle_Returns400WithDagCycleCode()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a", ("src", "@b")),
            ComputeNode("b", ("src", "@a")));

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dag-cycle", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_MalformedJson_Returns400WithInvalidRequestBody()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = new StringContent("{not:valid", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-request-body", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_DuplicateNodeIds_Returns400WithoutOpeningStream()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a"),
            ComputeNode("a"));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("duplicate-node-id", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_BlankNodeId_Returns400WithInvalidNodeId()
    {
        // Blank ids previously planned and applied, emitting blank `nodeId`
        // values in the event stream. The resolver now rejects them with a
        // stable `invalid-node-id` diagnostic that surfaces at the REST
        // boundary.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode(""));

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-node-id", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_NullNodesCollection_Returns400WithInvalidRequestBody()
    {
        // The DTO default is an empty list, but System.Text.Json preserves an
        // explicit null. Previously the transport swallowed it and the
        // downstream enumeration tripped a NullReferenceException — today the
        // boundary validates and maps to the documented 400 contract.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "grammar/1.0",
            ["processFamilyVersion"] = "family/1.0",
            ["nodes"] = null
        };

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-request-body", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/plan")]
    public async Task Plan_NullNodeEntry_Returns400WithInvalidNodeId()
    {
        // `[null]` deserialises as a real null entry inside the nodes list. The
        // boundary rejects it with a stable diagnostic instead of throwing NRE
        // at the first enumeration.
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "grammar/1.0",
            ["processFamilyVersion"] = "family/1.0",
            ["nodes"] = new object?[] { null }
        };

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-node-id", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_NullNodeEntry_Returns400WithoutOpeningStream()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "grammar/1.0",
            ["processFamilyVersion"] = "family/1.0",
            ["nodes"] = new object?[] { null }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-node-id", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_BlankNodeId_Returns400WithoutOpeningStream()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("   "));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid-node-id", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_Cycle_Returns400WithoutOpeningStream()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a", ("src", "@b")),
            ComputeNode("b", ("src", "@a")));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dag-cycle", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_ReservedDatasetKind_StreamsFailedWithSpecKindNotInS1()
    {
        // Production DI registers a placeholder ReservedSpecResourceStateStore
        // for Dataset/Service/App. Apply must still reject the node kind-based
        // so the DI placeholder cannot mask the S1 gap.
        using var factory = CreateLicensedFactory(HonuaEdition.Pro);
        using var client = factory.CreateClient();

        var datasetNode = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "d",
            ["kind"] = "Dataset",
            ["op"] = "dataset.create"
        };
        var document = BuildDocument(datasetNode);

        var events = await CollectSseEventsAsync(client, document);
        var failed = events.Single(e => e.Kind == "Failed");
        Assert.Equal("d", failed.Payload.GetProperty("nodeId").GetString());
        Assert.Equal(
            "spec-kind-not-in-s1",
            failed.Payload.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_WithCommunityLicense_ReturnsPaymentRequired()
    {
        using var factory = CreateLicensedFactory(HonuaEdition.Community);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(BuildDocument(ComputeNode("a")))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(FeatureCatalog.AiSpecApplyKey, body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_WithoutEventStreamAccept_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("a"));
        using var response = await client.PostAsync("/v1/spec/apply", JsonContent(document));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("accept-required", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    [FlakyTest("SSE response headers occasionally null mid-stream under combined Testcontainers/fixture load — tracked in #812")]
    public async Task Apply_LinearChain_StreamsSseEvents_AndSucceeds()
    {
        using var factory = CreateLicensedFactory(HonuaEdition.Pro);
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        var events = await CollectSseEventsAsync(client, document);
        var kinds = events.Select(e => e.Kind).ToArray();

        Assert.Contains("ApplyStarted", kinds);
        Assert.Contains("ApplyCompleted", kinds);
        Assert.Equal(2, kinds.Count(k => k == "Succeeded"));

        var applyCompleted = events.Single(e => e.Kind == "ApplyCompleted");
        var summary = applyCompleted.Payload.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalNodes").GetInt32());
        Assert.Equal(2, summary.GetProperty("ranNodes").GetInt32());
        Assert.Equal(0, summary.GetProperty("cachedNodes").GetInt32());
        Assert.False(summary.GetProperty("cancelled").GetBoolean());
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_RerunSameDocument_YieldsCachedEventsForEveryNode()
    {
        using var factory = CreateLicensedFactory(HonuaEdition.Pro);
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        var first = await CollectSseEventsAsync(client, document);
        var firstSummary = first.Single(e => e.Kind == "ApplyCompleted")
            .Payload.GetProperty("summary");
        Assert.Equal(2, firstSummary.GetProperty("ranNodes").GetInt32());

        var second = await CollectSseEventsAsync(client, document);
        var secondSummary = second.Single(e => e.Kind == "ApplyCompleted")
            .Payload.GetProperty("summary");
        Assert.Equal(0, secondSummary.GetProperty("ranNodes").GetInt32());
        Assert.Equal(2, secondSummary.GetProperty("cachedNodes").GetInt32());

        var cachedKinds = second.Where(e => e.Kind == "Cached").Select(e =>
            e.Payload.GetProperty("nodeId").GetString()).ToArray();
        Assert.Contains("a", cachedKinds);
        Assert.Contains("b", cachedKinds);
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("POST /v1/spec/cancel")]
    public async Task Cancel_UnknownToken_Returns404WithApplyTokenUnknown()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/spec/cancel",
            new StringContent("{\"applyToken\":\"does-not-exist\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("apply-token-unknown", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("POST /v1/spec/cancel")]
    public async Task Cancel_MissingToken_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/spec/cancel",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("apply-token-missing", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /v1/spec/artifact/{hash}")]
    public async Task Artifact_UnknownHash_Returns404WithArtifactNotFound()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/spec/artifact/deadbeef");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("artifact-not-found", payload.RootElement.GetProperty("code").GetString());
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /v1/spec/artifact/{hash}")]
    public async Task Artifact_AfterApply_ReturnsStoredBytes()
    {
        using var factory = CreateLicensedFactory(HonuaEdition.Pro);
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("a"));
        var events = await CollectSseEventsAsync(client, document);

        var succeeded = events.Single(e => e.Kind == "Succeeded");
        var contentHash = succeeded.Payload.GetProperty("contentHash").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", contentHash);

        using var response = await client.GetAsync($"/v1/spec/artifact/{contentHash}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentHash, response.Headers.GetValues("X-Spec-Content-Hash").Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrEmpty(body), "Artifact body should not be empty.");

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("a", doc.RootElement.GetProperty("nodeId").GetString());
        Assert.Equal(contentHash, doc.RootElement.GetProperty("contentHash").GetString());
    }

    // ---- AI-operations guardrail ladder (#1631) -------------------------

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_CommunityEdition_RunsDirectlyWithoutGuardrailGate()
    {
        // Community posture: agent-initiated apply runs with no enforced
        // guardrails, so the request proceeds past the ladder. With a valid
        // grammar/process-family it streams SSE rather than returning 402.
        //
        // The guardrail ladder is orthogonal to the spec-apply *execution*
        // entitlement (ai.spec-apply, Pro), which gates apply independently of the
        // guardrail posture. Grant that single entitlement on top of the Community
        // edition so this test isolates the guardrail-ladder behaviour under test
        // from the execution gate (whose closed-gate 402 is covered by
        // Apply_ProEditionWithoutEntitlement_Returns402).
        using var factory = WithLicense(new TestLicenseEntitlementService(
            HonuaEdition.Community,
            LicenseValidationState.Valid,
            entitlements:
            [
                .. LicenseTestSupport.ActiveEntitlementsFor(HonuaEdition.Community),
                FeatureCatalog.AiSpecApplyKey,
            ]));
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("a"));
        var events = await CollectSseEventsAsync(client, document);

        Assert.Contains("ApplyStarted", events.Select(e => e.Kind));
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_ProEditionWithoutEntitlement_Returns402()
    {
        // Pro guardrail level requires the validation-layer entitlement. A
        // snapshot carrying no entitlements (validation failure) leaves the gate
        // closed, so apply is rejected with 402 before the stream opens.
        using var factory = WithLicense(new TestLicenseEntitlementService(
            HonuaEdition.Pro, LicenseValidationState.Valid, entitlements: []));
        using var client = factory.CreateClient();

        var response = await SendApplyAsync(client, BuildDocument(ComputeNode("a")));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        response.Dispose();
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_ProEditionWithEntitlement_PassesGuardrailGate()
    {
        // Pro with the agent-operations entitlement satisfies the validation
        // layer, so apply proceeds and streams SSE.
        using var factory = WithEdition(HonuaEdition.Pro);
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("a"));
        var events = await CollectSseEventsAsync(client, document);

        Assert.Contains("ApplyStarted", events.Select(e => e.Kind));
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    public async Task Apply_EnterpriseEdition_PassesValidationAndApprovalGates()
    {
        // Enterprise carries both the validation-layer and approval-workflow
        // entitlements, so the full ladder is satisfied and apply proceeds.
        using var factory = WithEdition(HonuaEdition.Enterprise);
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("a"));
        var events = await CollectSseEventsAsync(client, document);

        Assert.Contains("ApplyStarted", events.Select(e => e.Kind));
    }

    // ---- helpers --------------------------------------------------------

    private static WebApplicationFactory<Program> WithEdition(HonuaEdition edition)
        => WithLicense(new TestLicenseEntitlementService(edition));

    private static WebApplicationFactory<Program> WithLicense(TestLicenseEntitlementService license)
    {
        var factory = new TestWebApplicationFactory();
        return factory.WithWebHostBuilder(builder =>
        {
            ApplySpecAuthBypass(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILicenseEntitlementService>(license);
                services.AddSingleton<ILicenseStatusProvider>(license);
            });
        });
    }

    /// <summary>
    /// The <c>/v1/spec/*</c> endpoints are admin-gated (#1984). Enable the
    /// Test-only development auth bypass — the same posture the shared
    /// integration fixtures use — so these endpoint tests authenticate without
    /// attaching an API key at every call site.
    /// </summary>
    private static void ApplySpecAuthBypass(IWebHostBuilder builder)
    {
        builder.UseSetting("HONUA_DEV_AUTH", "true");
        builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
    }

    /// <summary>
    /// Creates the default admin-authorized test host for the spec surface.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory()
        => new TestWebApplicationFactory().WithWebHostBuilder(ApplySpecAuthBypass);

    private static async Task<HttpResponseMessage> SendApplyAsync(HttpClient client, object document)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await client.SendAsync(request);
    }

    private static StringContent JsonContent(object document) =>
        new(JsonSerializer.Serialize(document), Encoding.UTF8, "application/json");

    private static Dictionary<string, object?> ComputeNode(string id, params (string Key, string Value)[] inputs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in inputs)
        {
            map[k] = v;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["kind"] = "Compute",
            ["op"] = "compute.noop",
            ["inputs"] = map
        };
    }

    private static Dictionary<string, object?> BuildDocument(params Dictionary<string, object?>[] nodes) =>
        new(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "grammar/1.0",
            ["processFamilyVersion"] = "family/1.0",
            ["nodes"] = nodes
        };

    private static WebApplicationFactory<Program> CreateLicensedFactory(HonuaEdition edition)
    {
        var license = new TestLicenseEntitlementService(edition);
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                ApplySpecAuthBypass(builder);
                builder.ConfigureServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<ILicenseEntitlementService>(license));
                    services.Replace(ServiceDescriptor.Singleton<ILicenseStatusProvider>(license));
                });
            });
    }

    private static async Task<IReadOnlyList<SseFrame>> CollectSseEventsAsync(
        HttpClient client,
        object document,
        TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var frames = new List<SseFrame>();
        string? currentEvent = null;
        string? currentData = null;
        while (!cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (currentEvent is not null && currentData is not null)
                {
                    var doc = JsonDocument.Parse(currentData);
                    frames.Add(new SseFrame(currentEvent, doc.RootElement.Clone()));
                    doc.Dispose();
                }

                currentEvent = null;
                currentData = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                currentData = line["data: ".Length..];
            }
        }

        return frames;
    }

    private readonly record struct SseFrame(string Kind, JsonElement Payload);
}
