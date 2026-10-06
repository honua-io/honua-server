// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Verifies that the optional application prefix participates in normal endpoint routing.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class ArcGisPathBaseTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IStartupFilter, ArcGisPathBaseStartupFilter>();
        builder.Services.AddHonuaHeadRequestSupport();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseHonuaHeadRequestMethod();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseHonuaHeadRequestGetSemantics();
        _app.MapGet("/rest/info", (HttpContext context) => Results.Text(
            $"{BaseUrlResolver.GetBaseUrl(context)}|{context.Request.Path}|{context.Request.QueryString}"));
        _app.MapGet("/advertised", (HttpContext context) => Results.Text(
            BaseUrlResolver.PreserveToken(context.Request, "https://localhost/wfs")));
        _app.MapPost("/services", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            return Results.Text($"{context.Request.PathBase}|{context.Request.Path}|{body}", "text/xml");
        });
        _app.MapGet("/admin/services/sample.MapServer", () => Results.Text("compatibility metadata"));
        _app.MapGet("/api/v1/admin/services", () => Results.Text("protected"))
            .RequireAuthorization();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    [UnitTest]
    public async Task Issue5520_AdvertisedUrl_PreservesOnlyRequestToken()
    {
        var credentialed = await _client.GetStringAsync("/advertised?token=a%26b&SERVICE=WFS");
        var anonymous = await _client.GetStringAsync("/advertised?SERVICE=WFS");

        credentialed.Should().Be("https://localhost/wfs?token=a%26b");
        anonymous.Should().Be("https://localhost/wfs");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [UnitTest]
    public async Task RestPrefix_PreservesQueryAndPublicBaseUrl()
    {
        var root = await _client.GetStringAsync("/rest/info?f=json");
        var prefixed = await _client.GetStringAsync("/arcgis/rest/info?f=json");

        root.Should().Be("http://localhost|/rest/info|?f=json");
        prefixed.Should().Be("http://localhost/arcgis|/rest/info|?f=json");
        (await _client.GetStringAsync("/rest/info?f=json")).Should().Be(root);
    }

    [UnitTest]
    public async Task SoapPrefix_PreservesPostBody()
    {
        const string body = "<Envelope><Body><GetMessageVersion/></Body></Envelope>";
        using var content = new StringContent(body, Encoding.UTF8, "text/xml");
        using var response = await _client.PostAsync("/arcgis/services", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be($"/arcgis|/services|{body}");
    }

    [UnitTest]
    public async Task CompatibilityAdminPrefix_UsesExistingEndpoint()
    {
        (await _client.GetStringAsync("/arcgis/admin/services/sample.MapServer"))
            .Should().Be(await _client.GetStringAsync("/admin/services/sample.MapServer"));
    }

    [UnitTest]
    public async Task ProtectedAdminPrefix_RequiresSameAuthorization()
    {
        foreach (var prefix in new[] { "", "/arcgis" })
        {
            using var response = await _client.GetAsync(prefix + "/api/v1/admin/services");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [UnitTest]
    public async Task HeadPrefix_MatchesGetHeadersWithoutBody()
    {
        using var get = await _client.GetAsync("/arcgis/rest/info?f=json");
        using var request = new HttpRequestMessage(HttpMethod.Head, "/arcgis/rest/info?f=json");
        using var head = await _client.SendAsync(request);

        head.StatusCode.Should().Be(get.StatusCode);
        head.Content.Headers.ContentLength.Should().Be((await get.Content.ReadAsByteArrayAsync()).Length);
        (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Prefix_MatchesOneCompletePathSegment()
    {
        using var nearMatch = await _client.GetAsync("/arcgis-other/rest/info");
        using var repeated = await _client.GetAsync("/arcgis/arcgis/rest/info");

        nearMatch.StatusCode.Should().Be(HttpStatusCode.NotFound);
        repeated.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
