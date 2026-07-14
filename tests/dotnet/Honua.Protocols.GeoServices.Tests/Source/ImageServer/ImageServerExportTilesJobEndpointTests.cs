// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Direct endpoint coverage for the asynchronous (Compact Cache V2 / TPKX) ImageServer exportTiles
/// job lifecycle: submission, status projection, validation, and sanitized not-found scoping.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerExportTilesJobEndpointTests
{
    private const int TestLayerId = 0;

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportTiles")]
    [Endpoint("GET /rest/services/{id}/ImageServer/jobs/{jobId}")]
    public async Task ExportTiles_CompactV2_SubmitsDurableJobAndProjectsStatus()
    {
        var fixture = await CreateDurableFixtureAsync();
        try
        {
            var submit = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportTiles?f=json" +
                "&storageFormatType=esriMapCacheStorageModeCompactV2" +
                "&exportExtent=-180,-85,180,85&exportExtentSR=4326&levels=0,1,2&format=png&maxTiles=1000");

            var submitBody = await submit.Content.ReadAsStringAsync();
            submitBody.Should().Contain("esriJobSubmitted", $"submit response was: {submitBody}");
            var submitted = JsonSerializer.Deserialize(
                submitBody,
                ImageServerJsonContext.Default.ImageServerExportTilesJobSubmitResponse);
            submitted!.JobStatus.Should().Be("esriJobSubmitted");
            submitted.JobId.Should().NotBeNullOrEmpty();

            var status = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/jobs/{submitted.JobId}");
            status.StatusCode.Should().Be(HttpStatusCode.OK);
            var statusBody = JsonSerializer.Deserialize(
                await status.Content.ReadAsStringAsync(),
                ImageServerJsonContext.Default.ImageServerExportTilesJobStatusResponse);
            statusBody!.JobId.Should().Be(submitted.JobId);
            statusBody.JobStatus.Should().Be("esriJobSubmitted");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportTiles")]
    public async Task ExportTiles_CompactV2_SingleZoomLevel_ReturnsError()
    {
        var fixture = await CreateDurableFixtureAsync();
        try
        {
            var response = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/exportTiles?f=json" +
                "&storageFormatType=esriMapCacheStorageModeCompactV2" +
                "&exportExtent=-180,-85,180,85&exportExtentSR=4326&levels=0&format=png&maxTiles=1000");

            // The GeoServices error envelope is delivered with an error body; the durable path must
            // reject a single-level TPKX request rather than submitting a job.
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("esriJobSubmitted");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/jobs/{jobId}")]
    public async Task JobStatus_UnknownJob_ReturnsSanitizedNotFound()
    {
        var fixture = await CreateDurableFixtureAsync();
        try
        {
            var status = await fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/jobs/te-does-not-exist");
            var body = await status.Content.ReadAsStringAsync();
            body.Should().NotContain("esriJobSubmitted");
            body.ToLowerInvariant().Should().Contain("not found");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<WebAppFixture> CreateDurableFixtureAsync()
    {
        // Submission and status only read metadata + create/read the durable job record; tile
        // rendering happens later on the worker, so a bare raster-store double is sufficient here.
        var rasterStore = Substitute.For<IRasterStore>();

        var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.AddSingleton(rasterStore);
            // The durable lifecycle service resolves its store/queue leniently; wire in-memory
            // doubles so a submission is durably created without a Redis dependency.
            services.AddSingleton<IExecutionJobStore>(new InMemoryExecutionJobStore());
            services.AddSingleton<IJobQueue>(new InMemoryJobQueue());
        });
        await fixture.InitializeAsync();
        return fixture;
    }
}
