// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Scene;

/// <summary>
/// Integration tests for the read-only Esri I3S SceneServer serving endpoints
/// (#1202). Verifies Enterprise gating and the service/layer descriptor JSON
/// for a hosted fixture scene.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class I3sSceneServerEndpointTests : IAsyncLifetime
{
    private const string SceneId = "i3s-fixture";
    private const string ProtectedSceneId = "i3s-protected";
    private const string AdminPassword = "i3s-auth-test-key";

    private readonly WebAppFixture _enterpriseFixture;
    private readonly WebAppFixture _communityFixture;
    private readonly string _fixtureRoot;
    private HttpClient _authenticatedClient = null!;

    public I3sSceneServerEndpointTests()
    {
        _fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _enterpriseFixture = BuildFixture(HonuaEdition.Enterprise);
        _communityFixture = BuildFixture(HonuaEdition.Community);
    }

    private WebAppFixture BuildFixture(HonuaEdition edition)
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"Scenes:Datasets:0:Id"] = SceneId,
                        [$"Scenes:Datasets:0:Name"] = "I3S Fixture Scene",
                        [$"Scenes:Datasets:0:Description"] = "Static scene used by I3S serving tests",
                        [$"Scenes:Datasets:0:AssetRoot"] = _fixtureRoot,

                        // Protected scene — requires an authenticated principal so
                        // the I3S SceneServer access-policy enforcement is covered
                        // (mirrors the gRPC and HTTP tileset RBAC parity tests).
                        [$"Scenes:Datasets:1:Id"] = ProtectedSceneId,
                        [$"Scenes:Datasets:1:Name"] = "I3S Protected Scene",
                        [$"Scenes:Datasets:1:AssetRoot"] = _fixtureRoot,
                        [$"Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false",
                    });
                });
            })
            .ConfigureServices(services =>
                services.AddSingleton<ILicenseStatusProvider>(new StubLicenseStatusProvider(edition)));

        return fixture;
    }

    public async Task InitializeAsync()
    {
        await _enterpriseFixture.InitializeAsync();
        await _communityFixture.InitializeAsync();
        _authenticatedClient = _enterpriseFixture.CreateClient(
            c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public async Task DisposeAsync()
    {
        await _enterpriseFixture.DisposeAsync();
        await _communityFixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_EnterpriseEdition_ReturnsI3sServiceDescriptor()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("serviceName").GetString().Should().Be("I3S Fixture Scene");
        root.GetProperty("serviceVersion").GetString().Should().Be("1.7");
        var layers = root.GetProperty("layers").EnumerateArray().ToArray();
        layers.Should().ContainSingle();
        layers[0].GetProperty("layerType").GetString().Should().Be("3DObject");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_EnterpriseEdition_ReturnsThreeDObjectLayer()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("id").GetInt32().Should().Be(0);
        root.GetProperty("layerType").GetString().Should().Be("3DObject");
        root.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_UnknownLayerId_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer/layers/7");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_UnknownScene_Returns404()
    {
        var response = await _enterpriseFixture.Client.GetAsync("/scenes/does-not-exist/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_CommunityEdition_Returns403()
    {
        var response = await _communityFixture.Client.GetAsync($"/scenes/{SceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_ProtectedScene_WithoutAuth_ReturnsUnauthorized()
    {
        // The I3S SceneServer root must enforce the scene access policy: an
        // anonymous caller against a protected scene is denied before the
        // descriptor is built. Guards against a regression dropping the
        // AccessPolicyHelpers.RequireAccess call.
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_ProtectedScene_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _enterpriseFixture.Client.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer")]
    public async Task GetService_ProtectedScene_WithAuth_ReturnsI3sServiceDescriptor()
    {
        var response = await _authenticatedClient.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("serviceName").GetString().Should().Be("I3S Protected Scene");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /scenes/{sceneId}/SceneServer/layers/{layerId:int}")]
    public async Task GetLayer_ProtectedScene_WithAuth_ReturnsThreeDObjectLayer()
    {
        var response = await _authenticatedClient.GetAsync($"/scenes/{ProtectedSceneId}/SceneServer/layers/0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("layerType").GetString().Should().Be("3DObject");
    }

    private sealed class StubLicenseStatusProvider : ILicenseStatusProvider
    {
        private readonly HonuaEdition _edition;

        public StubLicenseStatusProvider(HonuaEdition edition) => _edition = edition;

        public LicenseStatus GetCurrentStatus() =>
            new(_edition, IsValid: true, ExpiresAt: null, LicensedTo: "test");

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicenseUploadResult(false, "Stub does not support upload."));
    }
}
