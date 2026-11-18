// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Admin;

// Enabling or disabling layers changes what MCP sessions can list, so both toggle routes must
// broadcast resource and tool list_changed once the change has committed.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerEnableMcpNotificationTests
{
    private static readonly Guid ConnectionId = Guid.NewGuid();

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{storageLayerId}/enabled")]
    public async Task SetLayerEnabled_BroadcastsResourceAndToolListChanged()
    {
        var notifications = Substitute.For<IMcpNotificationPublisher>();
        var status = await PutEnabledAsync("layers/42/enabled?serviceName=test", notifications);

        status.Should().Be(HttpStatusCode.OK);
        await notifications.Received(1).BroadcastResourcesListChanged();
        await notifications.Received(1).BroadcastToolsListChanged();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/enabled")]
    public async Task SetServiceLayersEnabled_BroadcastsResourceAndToolListChanged()
    {
        var notifications = Substitute.For<IMcpNotificationPublisher>();
        var status = await PutEnabledAsync("layers/enabled?serviceName=test", notifications);

        status.Should().Be(HttpStatusCode.OK);
        await notifications.Received(1).BroadcastResourcesListChanged();
        await notifications.Received(1).BroadcastToolsListChanged();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /api/v1/admin/connections/{id}/layers/{storageLayerId}/enabled")]
    public async Task SetLayerEnabled_WhenBroadcastFails_StillReturnsTheChangedLayer()
    {
        var notifications = Substitute.For<IMcpNotificationPublisher>();
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
        notifications.BroadcastResourcesListChanged()
            .Returns(_ => ValueTask.FromException<int>(new InvalidOperationException("stream store offline")));
#pragma warning restore CA2012
        var status = await PutEnabledAsync("layers/42/enabled?serviceName=test", notifications);

        status.Should().Be(HttpStatusCode.OK);
        await notifications.Received(1).BroadcastToolsListChanged();
    }

    private static async Task<HttpStatusCode> PutEnabledAsync(string route, IMcpNotificationPublisher notifications)
    {
        var fixture = CreateFixture(notifications);
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().PutAsJsonAsync(
                $"/api/v1/admin/connections/{ConnectionId:D}/{route}",
                new LayerEnabledRequest { Enabled = false });
            return response.StatusCode;
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(IMcpNotificationPublisher notifications)
    {
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns("resolved-connection");
        var migrations = Substitute.For<IDatabaseMigrationRunner>();
        migrations.RunMigrationsAsync(
                Arg.Any<string>(), Arg.Any<System.Reflection.Assembly>(), Arg.Any<CancellationToken>())
            .Returns(DatabaseMigrationResult.Succeeded());
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing.SetLayerEnabledAsync(
                Arg.Any<string>(), 42, Arg.Any<string>(), false, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ChangedLayer());
        publishing.SetServiceLayersEnabledAsync(
                Arg.Any<string>(), Arg.Any<string>(), false, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([ChangedLayer()]);
        return new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(resolver)
            .ReplaceService<IDatabaseMigrationRunner>(migrations)
            .ReplaceService<ILayerPublishingService>(publishing)
            .ReplaceService<IMcpNotificationPublisher>(notifications);
    }

    private static PublishedLayerSummary ChangedLayer() => new()
    {
        StorageLayerId = 42,
        LayerName = "Small Layer",
        Schema = "public",
        Table = "small_table",
        GeometryType = "Point",
        ServiceName = "test",
    };
}
