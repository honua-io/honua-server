// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

/// <summary>
/// An external host can supply PathBase independently of the optional ArcGIS alias.
/// Reject unmounted paths so dereferencing a broken advertised URL cannot pass by accident.
/// </summary>
[Collection("Database.CoreFeatureStore")]
public sealed class MountedGeoServicesUrlTests : IAsyncLifetime
{
    private const string Mount = "/mounted";
    private WebAppFixture? _fixture;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _fixture?.DisposeAsync() ?? Task.CompletedTask;

    [IntegrationTheory]
    [InlineData("/")]
    [InlineData("/rest")]
    [InlineData("/arcgis")]
    [Protocol(TestProtocols.GeoservicesCatalog)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /")]
    [Endpoint("GET /rest")]
    [Endpoint("GET /arcgis")]
    public async Task SiteRoot_ExternalMount_RedirectsInsideApplication(string route)
    {
        var fixture = await CreateFixtureAsync();
        using var client = fixture.CreateClient(allowAutoRedirect: false);
        using var response = await client.GetAsync(Mount + route);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(Mount + "/rest/services");
        using var catalog = await client.GetAsync(response.Headers.Location);
        catalog.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [InlineData(null, Mount)]
    [InlineData("https://public.example/mounted", "https://public.example/mounted")]
    [InlineData("https://public.example", "https://public.example")]
    [InlineData("https://public.example/external", "https://public.example/external")]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.QueryAttachments)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments")]
    public async Task AttachmentUrl_ExternalMount_UsesConfiguredBaseExactlyOnce(string? publicBaseUrl, string expectedBase)
    {
        var fixture = await CreateFixtureAsync(publicBaseUrl);
        var storage = fixture.GetService<ICloudFileStorage>();
        await AttachmentTestData.SeedAsync(fixture.Postgres, storage, 0, 1);
        try
        {
            using var response = await fixture.Client.GetAsync(
                $"{Mount}/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/queryAttachments?objectIds=1&returnUrl=true");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var attachment = body.RootElement.GetProperty("attachmentGroups")[0]
                .GetProperty("attachmentInfos").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "test1.txt");
            var url = attachment.GetProperty("url").GetString();
            url.Should().Be($"{expectedBase}/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/0/1/attachments/{attachment.GetProperty("id").GetInt64()}");
            if (publicBaseUrl is null)
            {
                using var download = await fixture.Client.GetAsync(url);
                download.StatusCode.Should().Be(HttpStatusCode.OK);
                (await download.Content.ReadAsByteArrayAsync()).Should().Equal(AttachmentTestData.SeededTextFileBytes.ToArray());
            }
        }
        finally
        {
            await AttachmentTestData.CleanupAsync(fixture.Postgres, storage, 0, 1);
        }
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task FeatureServer_ExternalMount_AdvertisesReachableVersionService()
    {
        var fixture = await CreateFixtureAsync();
        BranchVersioningPublicationFixture.ConfigureManagedPublications(fixture);
        using var response = await fixture.Client.GetAsync(
            $"{Mount}/rest/services/{BranchVersioningPublicationFixture.ServiceName}/FeatureServer?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var url = body.RootElement.GetProperty("versionManagementServerUrl").GetString();
        url.Should().Be($"{Mount}/rest/services/{BranchVersioningPublicationFixture.ServiceName}/VersionManagementServer");
        using var versionService = await fixture.Client.GetAsync(url);
        versionService.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTheory]
    [InlineData("reconcile")]
    [InlineData("post")]
    [Protocol(TestProtocols.VersionManagementServer)]
    [Operation(Operations.VersionManagement)]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/reconcile")]
    [Endpoint("POST /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/post")]
    [Endpoint("GET /rest/services/{serviceId}/VersionManagementServer/versions/{versionGuid}/jobs/{jobId}")]
    public async Task VersionJob_ExternalMount_ReturnsReachablePollingUrl(string operation)
    {
        var fixture = await CreateFixtureAsync();
        var serviceUrl = $"{Mount}/rest/services/{WebAppFixture.TestServiceId}/VersionManagementServer";
        using var createForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["versionName"] = $"admin.mounted_{operation}_{Guid.NewGuid():N}",
            ["accessPermission"] = "private",
            ["f"] = "json"
        });
        using var create = await fixture.Client.PostAsync(serviceUrl + "/create", createForm);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var versionGuid = created.RootElement.GetProperty("versionInfo").GetProperty("versionGuid").GetString();
        using var jobForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["async"] = "true", ["f"] = "json" });
        using var accepted = await fixture.Client.PostAsync($"{serviceUrl}/versions/{versionGuid}/{operation}", jobForm);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var job = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var jobId = job.RootElement.GetProperty("jobId").GetString();
        var statusUrl = job.RootElement.GetProperty("statusUrl").GetString();
        statusUrl.Should().Be($"{serviceUrl}/versions/{versionGuid}/jobs/{jobId}");
        using var poll = await fixture.Client.GetAsync(statusUrl);
        poll.StatusCode.Should().Be(HttpStatusCode.OK);
        using var polled = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
        polled.RootElement.GetProperty("jobId").GetString().Should().Be(jobId);
        polled.RootElement.GetProperty("statusUrl").GetString().Should().Be(statusUrl);
    }

    private async Task<WebAppFixture> CreateFixtureAsync(string? publicBaseUrl = null)
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise);
        _fixture = fixture;
        fixture.ConfigureWebHost(builder =>
        {
            builder.UseSetting("Public:BaseUrl", publicBaseUrl ?? string.Empty);
            builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", "true");
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ExternalMountStartupFilter()));
        });
        await fixture.InitializeAsync();
        return fixture;
    }

    private sealed class ExternalMountStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UsePathBase(Mount);
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.PathBase != new PathString(Mount))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await nextMiddleware(context).ConfigureAwait(false);
            });
            next(app);
        };
    }
}
