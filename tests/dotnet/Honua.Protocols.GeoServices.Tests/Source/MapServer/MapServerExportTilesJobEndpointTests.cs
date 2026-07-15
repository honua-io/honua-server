// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.MapServer;

/// <summary>
/// Direct endpoint coverage for the asynchronous (Compact Cache V2 / TPKX) MapServer exportTiles
/// job lifecycle: submission, status, cancellation, and result scoping.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class MapServerExportTilesJobEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/jobs/{jobId}")]
    [Endpoint("POST /rest/services/{serviceId}/MapServer/jobs/{jobId}/cancel")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/jobs/{jobId}/results/out_service_url")]
    public async Task ExportTiles_CompactV2_SubmitStatusCancelResult()
    {
        var fixture = await CreateDurableFixtureAsync();
        var serviceId = WebAppFixture.TestServiceId;
        try
        {
            var submit = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/MapServer/exportTiles?f=json" +
                "&storageFormatType=esriMapCacheStorageModeCompactV2" +
                "&exportExtent=-180,-85,180,85&exportExtentSR=4326&levels=0,1&maxTiles=1000");
            var submitBody = await submit.Content.ReadAsStringAsync();
            var submitted = JsonSerializer.Deserialize(submitBody, MapServerJsonContext.Default.ExportTilesJobSubmitResponse);
            submitted.Should().NotBeNull($"submit response was: {submitBody}");
            submitted!.JobStatus.Should().Be("esriJobSubmitted");
            var jobId = submitted.JobId;

            var status = await fixture.Client.GetAsync($"/rest/services/{serviceId}/MapServer/jobs/{jobId}");
            (await status.Content.ReadAsStringAsync()).Should().Contain(jobId);

            var cancel = await fixture.Client.PostAsync(
                $"/rest/services/{serviceId}/MapServer/jobs/{jobId}/cancel", content: null);
            (await cancel.Content.ReadAsStringAsync()).Should().Contain("esriJobCancelled");

            var result = await fixture.Client.GetAsync(
                $"/rest/services/{serviceId}/MapServer/jobs/{jobId}/results/out_service_url");
            (await result.Content.ReadAsStringAsync()).Should().NotContain("out_service_url\":{\"value");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<WebAppFixture> CreateDurableFixtureAsync()
    {
        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.AddSingleton<IExecutionJobStore>(new InMemoryExecutionJobStore());
            services.AddSingleton<IJobQueue>(new InMemoryJobQueue());
        });
        await fixture.InitializeAsync();
        return fixture;
    }
}
