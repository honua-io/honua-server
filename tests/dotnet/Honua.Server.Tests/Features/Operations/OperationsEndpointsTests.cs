// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// Endpoint coverage for the Operations Toolset HTTP surface: the descriptor catalog lists
/// over <c>GET /api/v1/operations</c>, and an operation submits over
/// <c>POST /api/v1/operations/{id}/submit</c> through the policy-gated dispatcher.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class OperationsEndpointsTests
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/operations")]
    public async Task ListOperations_ReturnsCatalog_WithServicePublishDescriptor()
    {
        var fixture = new WebAppFixture();
        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.CreateAdminClient().GetAsync("/api/v1/operations");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("catalogVersion").GetString().Should().NotBeNullOrEmpty();

            var operations = data.GetProperty("operations").EnumerateArray().ToArray();
            var publish = operations.Should().ContainSingle(op =>
                op.GetProperty("operationId").GetString() == "service.publish").Subject;
            publish.GetProperty("executionKind").GetString().Should().Be("Synchronous");
            publish.GetProperty("approvalModel").GetString().Should().Be("OperatorGate");
            publish.GetProperty("policy").GetProperty("supportsDryRun").GetBoolean().Should().BeTrue();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task SubmitOperation_ServicePublish_ReturnsCompletedHandle_WithMetadataRevision()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 99,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default"
            });

        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");

        var fixture = new WebAppFixture()
            .ReplaceService<ILayerPublishingService>(publishing)
            .ReplaceService<ISecureConnectionResolver>(resolver);
        await fixture.InitializeAsync();
        try
        {
            var body = new
            {
                connectionId = Guid.NewGuid().ToString(),
                serviceName = "default",
                parameters = new Dictionary<string, string>
                {
                    ["schema"] = "public",
                    ["table"] = "parcels",
                    ["layerName"] = "Parcels"
                }
            };

            var response = await fixture.CreateAdminClient()
                .PostAsJsonAsync("/api/v1/operations/service.publish/submit", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("operationId").GetString().Should().Be("service.publish");
            data.GetProperty("status").GetString().Should().Be("Completed");
            data.TryGetProperty("metadataRevision", out var revision).Should().BeTrue();
            revision.GetInt64().Should().BeGreaterThanOrEqualTo(0);

            await publishing.Received(1).PublishLayerAsync(
                Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/operations/handles/{handleId}")]
    public async Task GetHandleStatus_AfterSubmit_ReturnsCompletedStatus()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 123,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default"
            });

        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");

        var fixture = new WebAppFixture()
            .ReplaceService<ILayerPublishingService>(publishing)
            .ReplaceService<ISecureConnectionResolver>(resolver);
        await fixture.InitializeAsync();
        try
        {
            var client = fixture.CreateAdminClient();
            var body = new
            {
                connectionId = Guid.NewGuid().ToString(),
                serviceName = "default",
                parameters = new Dictionary<string, string>
                {
                    ["schema"] = "public",
                    ["table"] = "parcels",
                    ["layerName"] = "Parcels"
                }
            };

            var submit = await client.PostAsJsonAsync("/api/v1/operations/service.publish/submit", body);
            submit.StatusCode.Should().Be(HttpStatusCode.OK);
            using var submitDocument = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            var handleId = submitDocument.RootElement.GetProperty("data").GetProperty("handleId").GetString();
            handleId.Should().NotBeNullOrEmpty();

            var status = await client.GetAsync($"/api/v1/operations/handles/{handleId}");
            status.StatusCode.Should().Be(HttpStatusCode.OK);
            using var statusDocument = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            var statusData = statusDocument.RootElement.GetProperty("data");
            statusData.GetProperty("handleId").GetString().Should().Be(handleId);
            statusData.GetProperty("status").GetString().Should().Be("Completed");
            statusData.GetProperty("operationId").GetString().Should().Be("service.publish");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/operations/{id}/validate")]
    public async Task ValidateOperation_ServicePublish_DelegatesToValidator()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .ValidateTableForPublishAsync(Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = "public",
                Table = "parcels",
                ServiceName = "default"
            });

        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");

        var fixture = new WebAppFixture()
            .ReplaceService<ILayerPublishingService>(publishing)
            .ReplaceService<ISecureConnectionResolver>(resolver);
        await fixture.InitializeAsync();
        try
        {
            var body = new
            {
                connectionId = Guid.NewGuid().ToString(),
                serviceName = "default",
                parameters = new Dictionary<string, string>
                {
                    ["schema"] = "public",
                    ["table"] = "parcels",
                    ["layerName"] = "Parcels"
                }
            };

            var response = await fixture.CreateAdminClient()
                .PostAsJsonAsync("/api/v1/operations/service.publish/validate", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("isValid").GetBoolean().Should().BeTrue();
            data.GetProperty("status").GetString().Should().Be("valid");

            await publishing.Received(1).ValidateTableForPublishAsync(
                Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
