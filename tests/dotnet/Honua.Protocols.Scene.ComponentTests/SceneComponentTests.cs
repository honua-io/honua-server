// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Middleware;
using Honua.Protocols.Scene;
using Honua.TestKit.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Protocols.Scene.ComponentTests;

/// <summary>Production scene serving without a database or unrelated protocols.</summary>
[Protocol(ProtocolNames.Scene)]
public sealed class SceneComponentTests
{
    [IntegrationTest]
    [Endpoint("GET /scenes/{sceneId}/{*assetPath}")]
    public async Task PublicAsset_ConditionalAndRangeRequests_PreserveWireContract()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.GetAsync("/scenes/public/tileset.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/scenes/public/tileset.json");
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var unchanged = await host.Client.SendAsync(conditional);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);
        using var range = new HttpRequestMessage(HttpMethod.Get, "/scenes/public/tiles/0.b3dm");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 7);
        using var partial = await host.Client.SendAsync(range);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync()).Should().HaveCount(8);
    }

    [IntegrationTest]
    [Endpoint("GET /scenes/{sceneId}/tileset.json")]
    public async Task ProtectedAsset_DeniesAnonymous_AndUnrelatedRoutesAreAbsent()
    {
        await using var host = await StartAsync();
        using var denied = await host.Client.GetAsync("/scenes/protected/tileset.json");
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var route in new[] { "/odata/$metadata", "/stac", "/sta/v1.1/Things" })
        {
            using var missing = await host.Client.GetAsync(route);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    internal static Task<ComponentTestHost> StartAsync(string? assetRoot = null, string sceneId = "public")
    {
        var root = assetRoot ?? FindFixture();
        return ComponentTestHost.StartAsync(builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenes:Datasets:0:Id"] = sceneId,
                ["Scenes:Datasets:0:Name"] = "Public fixture",
                ["Scenes:Datasets:0:AssetRoot"] = root,
                ["Scenes:Datasets:1:Id"] = "protected",
                ["Scenes:Datasets:1:Name"] = "Protected fixture",
                ["Scenes:Datasets:1:AssetRoot"] = root,
                ["Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false"
            });
            builder.Services.AddScene(builder.Configuration);
            builder.Services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
            builder.Services.Configure<OutputCacheTtlOptions>(_ => { });
            OutputCachingRegistration.ConfigureOutputCaching(builder.Services, builder.Configuration);
            // The supported production disabled licensing mode, without test entitlements.
            builder.Services.AddSingleton<DisabledLicenseService>();
            builder.Services.AddSingleton<ILicenseEntitlementService>(sp => sp.GetRequiredService<DisabledLicenseService>());
            builder.Services.AddSingleton<ILicenseStatusProvider>(sp => sp.GetRequiredService<DisabledLicenseService>());
            builder.Services.AddApiKeyAuthentication(builder.Configuration);
            builder.Services.Configure<ApiKeyAuthenticationOptions>(options =>
            {
                options.EnvironmentName = "Production";
                options.AdminPassword = "Component-Scene-Only-8!Yq";
            });
        }, app =>
        {
            app.UseGlobalExceptionHandling();
            app.UseMiddleware<AuthenticationResponseCacheMiddleware>();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseWhen(context => OutputCachingRegistration.HasLiveOutputCacheEntitlements(context.RequestServices, false),
                static entitled => entitled.UseOutputCache());
            app.MapSceneEndpoints();
        });
    }

    private static string FindFixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "fixtures", "scenes", "fixture-tileset");
            if (Directory.Exists(path))
            {
                return path;
            }
        }
        throw new DirectoryNotFoundException("Canonical scene fixture was not found.");
    }
}
