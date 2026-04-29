// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Integration tests for gRPC ProcessService exercised through the full ASP.NET Core pipeline
/// with a real PostgreSQL database via <see cref="WebAppFixture"/>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcProcessServiceIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private GrpcChannel? _channel;
    private Proto.ProcessService.ProcessServiceClient? _client;
    private Metadata? _headers;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        var testServerHandler = _fixture.CreateHandler();
        var grpcWebHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, testServerHandler);
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = grpcWebHandler
        });
        _client = new Proto.ProcessService.ProcessServiceClient(_channel);

        _headers = new Metadata();
        if (_fixture.CurrentSchema is not null)
        {
            _headers.Add("X-Honua-Test-Schema", _fixture.CurrentSchema);
        }
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ValidatePlan")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/ValidatePlan")]
    public async Task ValidatePlan_WithValidPlan_ReturnsExecutable()
    {
        var request = new Proto.ValidatePlanRequest { Plan = CreateValidPlan() };

        var response = await _client!.ValidatePlanAsync(request, _headers);

        response.Should().NotBeNull();
        response.Valid.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/DryRunPlan")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/DryRunPlan")]
    public async Task DryRunPlan_WithValidPlan_ReturnsEstimation()
    {
        var plan = CreateValidPlan();
        plan.ExpectedOutputs.Add("feature_layer");

        var request = new Proto.DryRunPlanRequest { Plan = plan };

        var response = await _client!.DryRunPlanAsync(request, _headers);

        response.Should().NotBeNull();
        response.Result.EstimatedArtifacts.Should().ContainSingle(a => a.ArtifactClass == Proto.ArtifactClass.FeatureLayer);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlan")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/ExecutePlan")]
    public async Task ExecutePlan_ReturnsUnimplemented()
    {
        var request = new Proto.ExecutePlanRequest { Plan = CreateValidPlan() };

        var act = async () => await _client!.ExecutePlanAsync(request, _headers);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unimplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/ExecutePlanStream")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/ExecutePlanStream")]
    public async Task ExecutePlanStream_ReturnsUnimplemented()
    {
        var request = new Proto.ExecutePlanRequest { Plan = CreateValidPlan() };

        using var call = _client!.ExecutePlanStream(request, _headers);
        var act = async () => await call.ResponseStream.MoveNext(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unimplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /geospatial.v1.ProcessService/SubmitJob")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/SubmitJob")]
    public async Task SubmitJob_ThroughPipeline_ReturnsExpectedResponse()
    {
        var request = new Proto.SubmitJobRequest
        {
            Plan = CreateValidPlan(),
            Context = new Proto.ExecutionContext()
        };
        request.Context.Metadata["idempotency_key"] = $"integ-{Guid.NewGuid():N}";

        try
        {
            var response = await _client!.SubmitJobAsync(request, _headers);
            response.JobId.Should().NotBeNullOrWhiteSpace();
            response.State.Should().Be(Proto.JobState.Validated);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            // Redis not configured in test environment — expected
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJob")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/GetJob")]
    public async Task GetJob_WithEmptyJobId_ReturnsInvalidArgument()
    {
        var request = new Proto.GetJobRequest { JobId = "" };

        var act = async () => await _client!.GetJobAsync(request, _headers);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.ProcessService/GetJobResult")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/GetJobResult")]
    public async Task GetJobResult_WithEmptyJobId_ReturnsInvalidArgument()
    {
        var request = new Proto.GetJobResultRequest { JobId = "" };

        var act = async () => await _client!.GetJobResultAsync(request, _headers);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /geospatial.v1.ProcessService/CancelJob")]
    [InterfaceOperation(TestProtocols.Grpc, "geospatial.v1.ProcessService/CancelJob")]
    public async Task CancelJob_WithEmptyJobId_ReturnsInvalidArgument()
    {
        var request = new Proto.CancelJobRequest { JobId = "" };

        var act = async () => await _client!.CancelJobAsync(request, _headers);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    private static Proto.ExecutionPlan CreateValidPlan()
    {
        var plan = new Proto.ExecutionPlan
        {
            PlanId = "integ-plan-1",
            SpecVersion = "integ-intent-1",
            WorkflowFamily = Proto.WorkflowFamily.Analyze
        };
        var step = new Proto.PlanStep
        {
            StepId = "step-1",
            Kind = "geoprocess"
        };
        step.Inputs["processId"] = new Proto.ParameterValue { StringValue = "geometry.buffer" };
        step.Inputs["wkb"] = new Proto.ParameterValue { StringValue = "AAAA" };
        step.Inputs["srid"] = new Proto.ParameterValue { StringValue = "4326" };
        step.Inputs["distance"] = new Proto.ParameterValue { StringValue = "100" };
        plan.Steps.Add(step);
        return plan;
    }
}
