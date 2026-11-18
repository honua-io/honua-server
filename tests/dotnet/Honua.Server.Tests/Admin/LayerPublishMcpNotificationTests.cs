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

// A synchronous (201) publish must tell MCP sessions that the resource and tool lists changed,
// exactly as the publish job does for a queued (202) publish.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class LayerPublishMcpNotificationTests
{
    private static readonly Guid ConnectionId = Guid.NewGuid();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task SynchronousPublish_BroadcastsResourceAndToolListChanged()
    {
        var notifications = Substitute.For<IMcpNotificationPublisher>();
        var response = await PublishAsync(CompletedAdmission(), notifications);

        response.Status.Should().Be(HttpStatusCode.Created, response.Body);
        await notifications.Received(1).BroadcastResourcesListChanged();
        await notifications.Received(1).BroadcastToolsListChanged();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task QueuedPublish_LeavesTheBroadcastToThePublishJob()
    {
        var notifications = Substitute.For<IMcpNotificationPublisher>();
        var queued = new LayerPublishAdmission(
            LayerPublishAdmissionKind.Queued, null, "job-1", 20_000, "/api/v1/admin/layer-publish-jobs/job-1");
        var response = await PublishAsync(queued, notifications);

        response.Status.Should().Be(HttpStatusCode.Accepted, response.Body);
        await notifications.DidNotReceive().BroadcastResourcesListChanged();
        await notifications.DidNotReceive().BroadcastToolsListChanged();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task SynchronousPublish_WhenBroadcastFails_StillReportsTheCommittedLayer()
    {
        var notifications = Substitute.For<IMcpNotificationPublisher>();
#pragma warning disable CA2012 // NSubstitute setup for a ValueTask-returning member.
        notifications.BroadcastResourcesListChanged()
            .Returns(_ => ValueTask.FromException<int>(new InvalidOperationException("stream store offline")));
#pragma warning restore CA2012
        var response = await PublishAsync(CompletedAdmission(), notifications);

        response.Status.Should().Be(HttpStatusCode.Created, response.Body);
        await notifications.Received(1).BroadcastToolsListChanged();
    }

    private static async Task<(HttpStatusCode Status, string Body)> PublishAsync(
        LayerPublishAdmission admission,
        IMcpNotificationPublisher notifications)
    {
        var fixture = CreateFixture(admission, notifications);
        await fixture.InitializeAsync();
        try
        {
            using var response = await fixture.CreateAdminClient().PostAsJsonAsync(
                $"/api/v1/admin/connections/{ConnectionId:D}/layers",
                new PublishLayerRequest
                {
                    Schema = "public",
                    Table = "small_table",
                    LayerName = "Small Layer",
                    ServiceName = "test",
                    Enabled = true,
                });
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(
        LayerPublishAdmission admission,
        IMcpNotificationPublisher notifications)
    {
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns("resolved-connection");
        // The publish route migrates the resolved connection before admission.
        var migrations = Substitute.For<IDatabaseMigrationRunner>();
        migrations.RunMigrationsAsync(
                Arg.Any<string>(), Arg.Any<System.Reflection.Assembly>(), Arg.Any<CancellationToken>())
            .Returns(DatabaseMigrationResult.Succeeded());
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.SubmitAsync(
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<LayerPublishRequest>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(admission);
        return new WebAppFixture()
            .ReplaceService<ISecureConnectionResolver>(resolver)
            .ReplaceService<IDatabaseMigrationRunner>(migrations)
            .ReplaceService<ILayerPublishJobs>(jobs)
            .ReplaceService<IMcpNotificationPublisher>(notifications);
    }

    private static LayerPublishAdmission CompletedAdmission() => new(
        LayerPublishAdmissionKind.Completed,
        new PublishedLayerSummary
        {
            StorageLayerId = 42,
            LayerName = "Small Layer",
            Schema = "public",
            Table = "small_table",
            GeometryType = "Point",
            ServiceName = "test",
        },
        null,
        12,
        null);
}
