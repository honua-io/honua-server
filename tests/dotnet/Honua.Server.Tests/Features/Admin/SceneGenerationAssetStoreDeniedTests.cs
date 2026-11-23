// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Tests.Features.Infrastructure.Scene;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

// The object store refuses every scene asset write for lack of permission, as an install whose
// storage role does not cover the scene folder would.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class SceneGenerationAssetStoreDeniedTests : IAsyncLifetime
{
    private const string AdminPassword = "scene-asset-store-admin-key";

    private readonly string _outputRoot = Path.Join(Path.GetTempPath(), $"scene-denied-{Guid.NewGuid():N}");
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public SceneGenerationAssetStoreDeniedTests()
    {
        var deniedStore = new InMemoryCloudFileStorage
        {
            UploadFailure = UploadResult.CreateFailure("File upload failed.") with { IsAccessDenied = true }
        };
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("SceneGeneration:OutputRoot", _outputRoot);
                builder.ConfigureTestServices(services =>
                    services.Replace(ServiceDescriptor.Singleton<ICloudFileStorage>(deniedStore)));
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        if (Directory.Exists(_outputRoot))
        {
            Directory.Delete(_outputRoot, recursive: true);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/scenes/generate")]
    public async Task Generate_AssetStoreRefusesWrites_Returns500NamingStoragePermissions()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/admin/scenes/generate",
            new GenerateSceneRequest { LayerId = 1, SceneId = $"denied-{Guid.NewGuid():N}" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SCENE_ASSET_STORE_UNWRITABLE", body);
        Assert.Contains("object-store permissions", body);
        Assert.DoesNotContain("retry", body, StringComparison.OrdinalIgnoreCase);
    }
}
