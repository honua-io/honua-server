// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Spec;

/// <summary>
/// Integration tests for gRPC <c>geospatial.v1.SpecService</c> exercised through
/// the full ASP.NET Core pipeline via <see cref="TestWebApplicationFactory"/>.
/// Uses gRPC-Web transport (HTTP/1.1) since the in-memory test server does not
/// support HTTP/2. Proves the gRPC apply/plan/cancel surface ships end-to-end.
/// </summary>
[Protocol(Protocols.Grpc)]
public sealed class GrpcSpecServiceIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly GrpcChannel _channel;
    private readonly Proto.SpecService.SpecServiceClient _client;

    public GrpcSpecServiceIntegrationTests()
    {
        _factory = new TestWebApplicationFactory();
        var testServerHandler = _factory.Server.CreateHandler();
        var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, testServerHandler);
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = grpcWebHandler
        });
        _client = new Proto.SpecService.SpecServiceClient(_channel);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _factory.Dispose();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [InterfaceOperation(Protocols.Grpc, "geospatial.v1.SpecService/PlanSpec")]
    public async Task PlanSpec_SingleNodeDocument_ReturnsPlanWithContentHash()
    {
        var request = new Proto.PlanSpecRequest { Document = BuildDocument("node-a") };

        var response = await _client.PlanSpecAsync(request);

        response.Should().NotBeNull();
        response.Plan.Nodes.Should().ContainSingle();
        response.Plan.Nodes[0].NodeId.Should().Be("node-a");
        response.Plan.Nodes[0].ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.SpecService/ApplySpec")]
    [InterfaceOperation(Protocols.Grpc, "geospatial.v1.SpecService/ApplySpec")]
    public async Task ApplySpec_SingleNodeDocument_StreamsTerminalEvents()
    {
        var request = new Proto.ApplySpecRequest
        {
            Document = BuildDocument("node-a"),
            CacheMode = Proto.SpecCacheMode.ReadWrite,
            MaxConcurrency = 1
        };

        using var call = _client.ApplySpec(request);
        var kinds = new List<Proto.SpecApplyEventKind>();
        await foreach (var evt in call.ResponseStream.ReadAllAsync())
        {
            kinds.Add(evt.Kind);
        }

        kinds.Should().Contain(Proto.SpecApplyEventKind.ApplyStarted);
        kinds.Should().Contain(Proto.SpecApplyEventKind.ApplyCompleted);
        kinds.Should().Contain(Proto.SpecApplyEventKind.Succeeded);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.SpecService/ApplySpec")]
    public async Task ApplySpec_FatalDocumentError_RaisesInvalidArgumentBeforeStream()
    {
        // Cycles / duplicate ids / unresolved refs must surface as
        // InvalidArgument rather than opening an empty stream that ends with
        // ApplyCompleted and zero nodes.
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop",
            Inputs = { ["src"] = "@b" }
        });
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "b",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop",
            Inputs = { ["src"] = "@a" }
        });

        var request = new Proto.ApplySpecRequest { Document = document };

        var act = async () =>
        {
            using var call = _client.ApplySpec(request);
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        };

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("dag-cycle");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.SpecService/ApplySpec")]
    public async Task ApplySpec_MissingVersionFields_RaisesInvalidArgumentBeforeStream()
    {
        // Blank grammar/process-family versions collide in the content-hash
        // cache key; the planner rejects them with `version-skew`, and the
        // gRPC adapter surfaces that as InvalidArgument before opening the
        // server-streaming response.
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = string.Empty,
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop"
        });

        var request = new Proto.ApplySpecRequest { Document = document };

        var act = async () =>
        {
            using var call = _client.ApplySpec(request);
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        };

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("version-skew");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task PlanSpec_Cycle_RaisesInvalidArgument()
    {
        // gRPC PlanSpec must surface fatal plan diagnostics as InvalidArgument
        // instead of returning a successful response with error-severity
        // warnings. Admin tooling keys off the diagnostic code.
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop",
            Inputs = { ["src"] = "@b" }
        });
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "b",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop",
            Inputs = { ["src"] = "@a" }
        });

        var request = new Proto.PlanSpecRequest { Document = document };

        var act = async () => await _client.PlanSpecAsync(request);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("dag-cycle");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task PlanSpec_DuplicateNodeId_RaisesInvalidArgument()
    {
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop"
        });
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop"
        });

        var request = new Proto.PlanSpecRequest { Document = document };

        var act = async () => await _client.PlanSpecAsync(request);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("duplicate-node-id");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task PlanSpec_VersionSkew_RaisesInvalidArgument()
    {
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = string.Empty,
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop"
        });

        var request = new Proto.PlanSpecRequest { Document = document };

        var act = async () => await _client.PlanSpecAsync(request);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("version-skew");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task PlanSpec_UnspecifiedKind_RaisesInvalidArgument()
    {
        // Nodes must declare a kind explicitly; the transport mapping rejects
        // SPEC_RESOURCE_KIND_UNSPECIFIED with `unknown-kind` rather than
        // silently coercing to COMPUTE.
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Unspecified,
            Op = "compute.noop"
        });

        var request = new Proto.PlanSpecRequest { Document = document };

        var act = async () => await _client.PlanSpecAsync(request);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("unknown-kind");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.SpecService/ApplySpec")]
    public async Task ApplySpec_UnspecifiedKind_RaisesInvalidArgumentBeforeStream()
    {
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = Proto.SpecResourceKind.Unspecified,
            Op = "compute.noop"
        });

        var request = new Proto.ApplySpecRequest { Document = document };

        var act = async () =>
        {
            using var call = _client.ApplySpec(request);
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        };

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("unknown-kind");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task PlanSpec_UnknownNumericKind_RaisesInvalidArgument()
    {
        // Protobuf does not constrain enum values on the wire — a future
        // client (or a forward-rolled one) can send any int32. The transport
        // mapping must whitelist defined values rather than silently coerce
        // an undefined one into the default arm, which used to leak Compute.
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = (Proto.SpecResourceKind)999,
            Op = "compute.noop"
        });

        var request = new Proto.PlanSpecRequest { Document = document };

        var act = async () => await _client.PlanSpecAsync(request);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("unknown-kind");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /geospatial.v1.SpecService/ApplySpec")]
    public async Task ApplySpec_UnknownNumericKind_RaisesInvalidArgumentBeforeStream()
    {
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = "a",
            Kind = (Proto.SpecResourceKind)999,
            Op = "compute.noop"
        });

        var request = new Proto.ApplySpecRequest { Document = document };

        var act = async () =>
        {
            using var call = _client.ApplySpec(request);
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        };

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("unknown-kind");
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("POST /geospatial.v1.SpecService/CancelApply")]
    [InterfaceOperation(Protocols.Grpc, "geospatial.v1.SpecService/CancelApply")]
    public async Task CancelApply_UnknownToken_ReturnsCancelledFalse()
    {
        var request = new Proto.CancelApplyRequest { ApplyToken = "does-not-exist" };

        var response = await _client.CancelApplyAsync(request);

        response.Cancelled.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("POST /geospatial.v1.SpecService/CancelApply")]
    public async Task CancelApply_EmptyToken_ReturnsInvalidArgument()
    {
        var request = new Proto.CancelApplyRequest { ApplyToken = string.Empty };

        var act = async () => await _client.CancelApplyAsync(request);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    private static Proto.CanonicalSpecDocument BuildDocument(string nodeId)
    {
        var document = new Proto.CanonicalSpecDocument
        {
            GrammarVersion = "grammar/1.0",
            ProcessFamilyVersion = "family/1.0"
        };
        document.Nodes.Add(new Proto.CanonicalSpecNode
        {
            Id = nodeId,
            Kind = Proto.SpecResourceKind.Compute,
            Op = "compute.noop"
        });
        return document;
    }
}
