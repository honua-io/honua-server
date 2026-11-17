// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

// The operation catalog lists connectionId and serviceName as ordinary inputs, so callers that
// follow it send them inside "parameters". Either placement must reach the executor.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class OperationsEndpointsEnvelopeTests
{
    private const string ConnectionId = "5b0d3f8e-6c1a-4c55-9d0e-1f2a3b4c5d6e";

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/validate")]
    public async Task Validate_ValuesInsideParameters_ReachTopLevelRequest()
    {
        var (status, captured) = await ValidateAsync(new
        {
            parameters = new Dictionary<string, string>
            {
                ["connectionId"] = ConnectionId,
                ["serviceName"] = "parcels",
                ["schema"] = "public",
            },
        });

        status.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.ConnectionId.Should().Be(ConnectionId);
        captured.ServiceName.Should().Be("parcels");
        captured.Parameters.Should().ContainKey("serviceName", "operations that read serviceName from parameters keep it");
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/validate")]
    public async Task Validate_TopLevelOnly_IsUnchanged()
    {
        var (status, captured) = await ValidateAsync(new
        {
            connectionId = ConnectionId,
            serviceName = "parcels",
            parameters = new Dictionary<string, string> { ["schema"] = "public" },
        });

        status.Should().Be(HttpStatusCode.OK);
        captured!.ConnectionId.Should().Be(ConnectionId);
        captured.ServiceName.Should().Be("parcels");
        captured.Parameters.Should().NotContainKey("connectionId");
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/validate")]
    public async Task Validate_SameValueInBothPlaces_IsAccepted()
    {
        var (status, captured) = await ValidateAsync(new
        {
            connectionId = ConnectionId,
            parameters = new Dictionary<string, string> { ["connectionId"] = ConnectionId },
        });

        status.Should().Be(HttpStatusCode.OK);
        captured!.ConnectionId.Should().Be(ConnectionId);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/validate")]
    public async Task Validate_ConflictingValues_AreRefusedBeforeTheExecutor()
    {
        var (status, captured) = await ValidateAsync(new
        {
            connectionId = ConnectionId,
            parameters = new Dictionary<string, string> { ["connectionId"] = Guid.NewGuid().ToString() },
        });

        status.Should().Be(HttpStatusCode.BadRequest);
        captured.Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/validate")]
    public async Task Validate_BlankNestedValue_KeepsTopLevel()
    {
        var (status, captured) = await ValidateAsync(new
        {
            serviceName = "parcels",
            parameters = new Dictionary<string, string> { ["serviceName"] = " " },
        });

        status.Should().Be(HttpStatusCode.OK);
        captured!.ServiceName.Should().Be("parcels");
    }

    private static async Task<(HttpStatusCode Status, OperationRequest? Captured)> ValidateAsync(object body)
    {
        OperationRequest? captured = null;
        var invoker = Substitute.For<IOperationInvoker>();
        invoker.ValidateAsync(Arg.Do<OperationRequest>(request => captured = request), Arg.Any<CancellationToken>())
            .Returns(new OperationValidation { IsValid = true, Status = "valid", Messages = [] });

        var fixture = new WebAppFixture().ReplaceService<IOperationInvoker>(invoker);
        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.CreateAdminClient()
                .PostAsJsonAsync("/api/v1/operations/admin.layer.publish/validate", body);
            return (response.StatusCode, captured);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
