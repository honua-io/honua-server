// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Create)]
public sealed class DevelopmentPublicationAuditTests
{
    [IntegrationTest]
    [Endpoint("POST /mcp tools/call honua_publish_service")]
    public async Task Publish_DevelopmentHost_PersistsActorAndTimeInAdminAudit()
    {
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.ValidateTableForPublishAsync(Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TablePublishValidationResult { IsValid = true, Status = "valid", Schema = "public", Table = "parcels", ServiceName = "default" });
        publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary { LayerId = 321, LayerName = "parcels", Schema = "public", Table = "parcels", GeometryType = "Polygon", Srid = 4326, ServiceName = "default" });
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("Host=localhost;Database=test");

        var fixture = new WebAppFixture().UseEnvironment(Environments.Development)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ReplaceService(publisher).ReplaceService(resolver);
        await fixture.InitializeAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            using var publish = await admin.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new
                {
                    name = "honua_publish_service", arguments = new
                    {
                        connectionId = "11111111-1111-1111-1111-111111111111", schema = "public",
                        table = "parcels", layerName = "parcels", serviceName = "default",
                    },
                },
            });
            publish.StatusCode.Should().Be(HttpStatusCode.OK);
            using var receipt = JsonDocument.Parse(await publish.Content.ReadAsStringAsync());
            var result = receipt.RootElement.GetProperty("result").GetProperty("structuredContent");
            result.GetProperty("status").GetString().Should().Be("Completed", result.GetRawText());
            var operationId = result.GetProperty("operationInstanceId").GetString();
            operationId.Should().NotBeNullOrWhiteSpace();
            using var response = await admin.GetAsync($"/api/v1/admin/observability/audit?resourceId={Uri.EscapeDataString(operationId!)}&action=operation.completed");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var audit = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var events = audit.RootElement.GetProperty("items").EnumerateArray().ToArray();
            events.Should().ContainSingle();
            events[0].GetProperty("actor").GetString().Should().StartWith("apikey:api-key:");
            events[0].GetProperty("timestamp").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
