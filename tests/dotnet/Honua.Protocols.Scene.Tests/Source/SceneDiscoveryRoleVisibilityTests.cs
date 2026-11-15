// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Scene;

// Scene discovery follows a scene's role list for signed-in callers who are not administrators.
[Collection("Database")]
[Protocol(TestProtocols.Scene)]
public sealed class SceneDiscoveryRoleVisibilityTests : IAsyncLifetime
{
    private const string ReaderRole = "scene-reader";
    private readonly WebAppFixture _fixture;
    private HttpClient _reader = null!;
    private HttpClient _otherUser = null!;

    public SceneDiscoveryRoleVisibilityTests()
    {
        var fixtureRoot = SceneFixturePaths.ResolveFixtureRoot();

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scenes:Datasets:0:Id"] = SceneFixturePaths.FixtureSceneId,
                        ["Scenes:Datasets:0:Name"] = "Public Fixture",
                        ["Scenes:Datasets:0:AssetRoot"] = fixtureRoot,
                        ["Scenes:Datasets:1:Id"] = SceneFixturePaths.ProtectedSceneId,
                        ["Scenes:Datasets:1:Name"] = "Role Fixture",
                        ["Scenes:Datasets:1:AssetRoot"] = fixtureRoot,
                        ["Scenes:Datasets:1:AccessPolicy:AllowAnonymous"] = "false",
                        ["Scenes:Datasets:1:AccessPolicy:AllowedRoles:0"] = ReaderRole
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                    services.PostConfigureAll<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _reader = CreateUserClient("reader", ReaderRole);
        _otherUser = CreateUserClient("other-user");
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes")]
    public async Task ListScenes_RoleRestrictedScene_ListedOnlyForCallerWithRole()
    {
        var readerSceneIds = await ListSceneIdsAsync(_reader);
        var otherSceneIds = await ListSceneIdsAsync(_otherUser);

        readerSceneIds.Should().Contain(SceneFixturePaths.ProtectedSceneId);
        otherSceneIds.Should().NotContain(SceneFixturePaths.ProtectedSceneId);
        otherSceneIds.Should().Contain(SceneFixturePaths.FixtureSceneId);
    }

    [IntegrationTheory]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes/{sceneId}")]
    [Endpoint("GET /api/scenes/{sceneId}/resolve")]
    [InlineData("")]
    [InlineData("/resolve")]
    public async Task DescribeScene_RoleRestrictedScene_ForbiddenWithoutRole(string suffix)
    {
        var path = $"/api/scenes/{SceneFixturePaths.ProtectedSceneId}{suffix}";

        using var reader = await _reader.GetAsync(path);
        using var otherUser = await _otherUser.GetAsync(path);

        reader.StatusCode.Should().Be(HttpStatusCode.OK);
        otherUser.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /api/scenes")]
    public async Task ListScenes_IncludeDisabled_ForbiddenForSignedInNonAdministrator()
    {
        using var response = await _reader.GetAsync("/api/scenes?includeDisabled=true");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private HttpClient CreateUserClient(string userName, params string[] roles)
        => _fixture.CreateClient(client =>
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userName);
            if (roles.Length > 0)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
            }
        });

    private static async Task<string?[]> ListSceneIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/scenes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("scenes").EnumerateArray()
            .Select(static scene => scene.GetProperty("id").GetString())
            .ToArray();
    }
}
