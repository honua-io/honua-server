// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Studio;

// A Lambda-hosted content-version read authenticates a managed API key against the credential
// registry and returns the version.
[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class LambdaContentVersionReadTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private string _managedKey = null!;

    public async Task InitializeAsync()
    {
        _fixture
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting(ControlPlaneEventHandlerHost.LambdaFunctionNameVariable, "honua-api");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                services.RemoveAll<IAdminApiKeyStore>();
                services.AddSingleton<IAdminApiKeyStore>(new InMemoryAdminApiKeyStore());
            });
        await _fixture.InitializeAsync();
        var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await keys.CreateAsync("journey-operator", ["admin:*"], null, "lambda-test", CancellationToken.None);
        _managedKey = created.Key;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions/{versionId}")]
    public async Task GetContentVersion_LambdaHostManagedKey_ReturnsOk()
    {
        _fixture.Services.GetRequiredService<IConfiguration>()[ControlPlaneEventHandlerHost.LambdaFunctionNameVariable]
            .Should().Be("honua-api");
        ControlPlaneEventHandlerHost.IsLambda(_fixture.Services.GetRequiredService<IConfiguration>()).Should().BeTrue();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>();
        using var body = JsonDocument.Parse("""{"where":"1=1"}""");
        var draft = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = $"lambda-read-{Guid.NewGuid():N}",
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Query,
                SchemaVersion = "1.0",
                Format = "studio_query_package.v1",
                Body = body.RootElement.Clone(),
            },
        });
        var version = await lifecycle.SaveDraftAsVersionAsync(draft.DraftId, "lambda thaw", "test", draft.Generation);
        version.Should().NotBeNull();

        using var client = _fixture.CreateClient(http => http.DefaultRequestHeaders.Add("X-API-Key", _managedKey));
        var response = await client.GetAsync(
            $"/api/v1/studio/content-items/{version!.ItemId:D}/versions/{version.VersionId:D}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
