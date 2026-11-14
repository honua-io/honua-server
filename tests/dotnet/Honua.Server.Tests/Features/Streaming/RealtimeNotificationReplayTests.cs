// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Realtime;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Realtime;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class RealtimeNotificationReplayTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/jobs")]
    public async Task Jobs_EventCommittedOnAnotherTask_ReplaysAfterCursorWithRequesterAuthorization()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var sender = Provider(harness);
        var cursor = await sender.AppendAsync(new RealtimeEventDraft(RealtimeFamily.Jobs, "mine", "job.status",
            RealtimeLogHarness.Payload(new { status = "Queued" })), null, CancellationToken.None);
        await sender.AppendAsync(new RealtimeEventDraft(RealtimeFamily.Jobs, "other", "job.status",
            RealtimeLogHarness.Payload(new { status = "Failed" })), null, CancellationToken.None);
        var next = await sender.AppendAsync(new RealtimeEventDraft(RealtimeFamily.Jobs, "mine", "job.status",
            RealtimeLogHarness.Payload(new { status = "Running", result = "private-result" })), null, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = Context("?jobId=mine", cursor, timeout);
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync("mine", context.User, Arg.Any<CancellationToken>()).Returns(
            new ExecutionJobRecord { OperationId = "mine", Status = ExecutionJobStatus.Running });

        await RealtimeNotificationEndpoints.HandleJobsAsync(context, Provider(harness), jobs);

        var output = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains($"id: {next}", output);
        Assert.Contains("Running", output);
        Assert.DoesNotContain("Queued", output);
        Assert.DoesNotContain("other", output);
        Assert.DoesNotContain("private-result", output);
        await jobs.Received(2).GetJobAsync("mine", context.User, Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/layers")]
    public async Task Layers_CatalogCommittedOnAnotherTask_ReplaysReadableDefinitionChange()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        var beforeGraph = new TestMetadataV2GraphBuilder().AddService("svc", "city", protocols: ServiceProtocols.All)
            .AddResource("resource", "roads").AddPublication("publication", "svc", "resource", layerIndex: 0).Build();
        var before = new MetadataV2GraphSnapshot(beforeGraph, "before", DateTimeOffset.UtcNow);
        var after = new MetadataV2GraphSnapshot(beforeGraph with
        {
            Resources = beforeGraph.Resources.Select(resource => resource with
            { Metadata = resource.Metadata with { Name = "roads-updated" } }).ToArray()
        }, "after", DateTimeOffset.UtcNow);
        var sender = Provider(harness);
        var cursor = await sender.AppendAsync(new RealtimeEventDraft(RealtimeFamily.Catalog, beforeGraph.Environment,
            "catalog.changed", RealtimeLogHarness.Payload(new { etag = "before", previousEtag = (string?)null })),
            null, CancellationToken.None);
        var next = await sender.AppendAsync(new RealtimeEventDraft(RealtimeFamily.Catalog, beforeGraph.Environment,
            "catalog.changed", RealtimeLogHarness.Payload(new { etag = "after", previousEtag = "before" })),
            null, CancellationToken.None);
        var graphs = Substitute.For<IMetadataV2GraphProvider>();
        var history = Substitute.For<IMetadataV2EnvironmentSnapshotReader>();
        graphs.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(after);
        history.GetByEtagAsync(beforeGraph.Environment, "before", Arg.Any<CancellationToken>()).Returns(before);
        history.GetByEtagAsync(beforeGraph.Environment, "after", Arg.Any<CancellationToken>()).Returns(after);
        using var services = new ServiceCollection().AddOptions()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>().BuildServiceProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = Context("?serviceId=city", cursor, timeout);
        context.RequestServices = services;

        await RealtimeNotificationEndpoints.HandleLayersAsync(context, Provider(harness), graphs, history);

        var output = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains($"id: {next}", output);
        Assert.Contains("event: layer.changed", output);
        Assert.Contains("\"layerId\":0", output);
        Assert.DoesNotContain("roads-updated", output);
    }

    private static PostgresRealtimeFanoutProvider Provider(RealtimeLogHarness harness) => new(
        harness.Log, harness.CreateDispatcher(), new RealtimePresenceBatcher(harness.Log));

    private static DefaultHttpContext Context(string query, RealtimePosition cursor, CancellationTokenSource lifetime)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "requester")], "test")),
            RequestAborted = lifetime.Token
        };
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers["Last-Event-ID"] = cursor.ToString();
        context.Response.Body = new CancelAfterFlushStream(lifetime);
        return context;
    }

    private sealed class CancelAfterFlushStream(CancellationTokenSource lifetime) : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (Length > 0)
            {
                lifetime.Cancel();
            }
            return Task.CompletedTask;
        }
    }
}
