// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure;

/// <summary>
/// Integration tests for temporary file endpoint authorization behavior.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class TemporaryFileEndpointTests : IDisposable
{
    private const string AdminPassword = "test-temp-admin-password";

    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"honua-temp-endpoint-tests-{Guid.NewGuid():N}");

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /temp/{fileId}")]
    public async Task GetTemporaryFile_WithAuthenticatedOwner_AllowsOwnerAndRejectsAnonymousReplay()
    {
        using var factory = CreateFactory();
        var owner = CreatePrincipal("admin");
        var fileId = await StoreTemporaryFileAsync(factory, owner);

        using var anonymousClient = factory.CreateClient();
        var anonymousResponse = await anonymousClient.GetAsync($"/temp/{fileId}");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var ownerClient = factory.CreateClient();
        ownerClient.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        var ownerResponse = await ownerClient.GetAsync($"/temp/{fileId}");
        ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ownerResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        (await ownerResponse.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /temp/{fileId}")]
    public async Task GetTemporaryFile_WithAnonymousStoredFile_AllowsAnonymousDownload()
    {
        using var factory = CreateFactory();
        var fileId = await StoreTemporaryFileAsync(factory, principal: null);

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/temp/{fileId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
        {
            Directory.Delete(_storageDirectory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TemporaryFiles:StorageDirectory"] = _storageDirectory,
                    ["TemporaryFiles:DefaultExpiration"] = "00:05:00",
                    ["TemporaryFiles:BaseUrl"] = "/temp"
                });
            });
        });
    }

    private static ClaimsPrincipal CreatePrincipal(string name)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, name),
            new Claim(ClaimTypes.Name, name)
        ], "test"));
    }

    private static async Task<string> StoreTemporaryFileAsync(
        WebApplicationFactory<Program> factory,
        ClaimsPrincipal? principal)
    {
        using var scope = factory.Services.CreateScope();
        var temporaryFileService = scope.ServiceProvider.GetRequiredService<ITemporaryFileService>();
        var url = await temporaryFileService.StoreTemporaryFileAsync(
            new byte[] { 1, 2, 3 },
            "image/png",
            principal: principal);

        return Path.GetFileName(url);
    }
}
