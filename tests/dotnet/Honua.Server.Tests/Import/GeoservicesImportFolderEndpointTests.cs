// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoservicesImportFolderEndpointTests
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_FolderSelection_PersistsServerPlannedIdentityAndResolvedScope(bool explicitRoot, bool namespaced)
    {
        var (jobs, captured) = CaptureJobs();
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise)
            .ReplaceService<IDistributedImportJobManager>(jobs);
        await fixture.InitializeAsync();
        var body = new Dictionary<string, object?>
        {
            ["serviceUrl"] = "https://example.com/arcgis/rest/services/Survey/Parcels/FeatureServer",
            ["layerId"] = 0,
            ["tableName"] = "planned_parcels",
            ["tenantId"] = "not-the-resolved-tenant"
        };
        if (explicitRoot) body["folderId"] = null;
        if (namespaced) body["namespace"] = "published";
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", body);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var request = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Target.Should().NotBeNull();
        request.ServiceName.Should().Be(request.Target!.ServiceName);
        request.Target.PublicName.Should().Be("Parcels");
        request.Target.FolderName.Should().Be(explicitRoot ? null : "Survey");
        request.Target.TenantId.Should().Be(namespaced ? "public" : null);
        request.Target.Namespace.Should().Be(namespaced ? "published" : null);
        request.Target.ServiceId.Should().NotBeNullOrWhiteSpace();
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var plan = accepted.GetProperty("plan");
        plan.GetProperty("serviceId").GetString().Should().Be(request.Target.ServiceId);
        plan.TryGetProperty("tenantId", out _).Should().BeFalse();
        var replay = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", new
        {
            serviceUrl = body["serviceUrl"],
            layerId = 0,
            tableName = "planned_parcels_retry",
            plan
        });
        replay.StatusCode.Should().Be(HttpStatusCode.Accepted, await replay.Content.ReadAsStringAsync());
        var replayed = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan");
        replayed.GetProperty("serviceId").GetString().Should().Be(request.Target.ServiceId);
        var initialFolder = plan.TryGetProperty("folderId", out var initialFolderId) ? initialFolderId.GetString() : null;
        var replayFolder = replayed.TryGetProperty("folderId", out var replayFolderId) ? replayFolderId.GetString() : null;
        replayFolder.Should().Be(initialFolder);
    }

    [IntegrationTheory]
    [InlineData("Outer/Inner", null, HttpStatusCode.BadRequest)]
    [InlineData("Survey", "missing-folder", HttpStatusCode.NotFound)]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    public async Task Start_InvalidFolderMapping_RefusesBeforeQueueing(string sourceFolder, string? targetFolder, HttpStatusCode expected)
    {
        var (jobs, captured) = CaptureJobs();
        await using var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise)
            .ReplaceService<IDistributedImportJobManager>(jobs);
        await fixture.InitializeAsync();
        var body = new Dictionary<string, object?>
        {
            ["serviceUrl"] = $"https://example.com/arcgis/rest/services/{sourceFolder}/Parcels/FeatureServer",
            ["layerId"] = 0,
            ["tableName"] = "invalid_folder_parcels"
        };
        if (targetFolder is not null) body["folderId"] = targetFolder;
        var response = await fixture.Client.PostAsJsonAsync("/api/v1/admin/import/geoservices/start", body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        captured.Task.IsCompleted.Should().BeFalse();
        await jobs.JobQueue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static (IDistributedImportJobManager, TaskCompletionSource<GeoservicesImportRequest>) CaptureJobs()
    {
        var captured = new TaskCompletionSource<GeoservicesImportRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = Substitute.For<IDistributedImportJobManager>();
        var requests = Substitute.For<IDistributedProgressStore<GeoservicesImportRequest>>();
        var progress = Substitute.For<IDistributedProgressStore<GeoservicesImportProgress>>();
        jobs.RequestStore.Returns(requests);
        jobs.ProgressStore.Returns(progress);
        jobs.JobQueue.Returns(Substitute.For<IDistributedJobQueueService>());
        jobs.LeaderElection.Returns(Substitute.For<IDistributedLeaderElection>());
        requests.SetProgressAsync(Arg.Any<string>(), Arg.Any<GeoservicesImportRequest>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured.TrySetResult(call.ArgAt<GeoservicesImportRequest>(1));
                return Task.CompletedTask;
            });
        return (jobs, captured);
    }
}
