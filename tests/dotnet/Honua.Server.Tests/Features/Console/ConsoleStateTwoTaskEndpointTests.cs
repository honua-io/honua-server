// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Console.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Console.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Console;

// Two hosts on one database are two tasks: Console state written through one is served by the other on its next
// request, through the real middleware, stores and endpoints.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ConsoleStateTwoTaskEndpointTests : IAsyncLifetime
{
    private const string AdminPassword = "console-two-task-admin-key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: true) },
    };

    // Deployed tasks share the data-protection key ring through the system database; the test environment keeps it
    // per host, so the two hosts are given one ring explicitly.
    private readonly DirectoryInfo _keyRing = Directory.CreateDirectory(
        Path.Join(Path.GetTempPath(), "honua-two-task-keys-" + Guid.NewGuid().ToString("N")));
    private WebAppFixture _taskA = null!;
    private WebAppFixture _taskB = null!;

    public async Task InitializeAsync()
    {
        _taskA = NewTaskHost(_keyRing);
        await _taskA.InitializeAsync();
        _taskB = NewTaskHost(_keyRing).ShareSchemaWith(_taskA);
        await _taskB.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _taskB.DisposeAsync();
        await _taskA.DisposeAsync();
        _keyRing.Delete(recursive: true);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("PUT /api/v1/console/content/{id}/share/access")]
    [Endpoint("POST /api/v1/console/content/{id}/share/link")]
    [Endpoint("GET /api/v1/console/share/link/{token}")]
    [Endpoint("DELETE /api/v1/console/content/{id}/share/link/{tokenId}")]
    public async Task PublicLinkMintedOnTaskA_ResolvesOnTaskB_UntilTaskARevokesIt()
    {
        using var adminA = Admin(_taskA);
        using var anonymousB = _taskB.CreateClient();
        var item = await CreateItemAsync(adminA);
        using (var access = await adminA.PutAsJsonAsync($"/api/v1/console/content/{item.Id}/share/access",
            new UpdateShareAccessRequest { AccessTier = ConsoleShareAccessTier.PublicLink }, JsonOptions))
        {
            access.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var minted = await adminA.PostAsJsonAsync($"/api/v1/console/content/{item.Id}/share/link", new MintPublicLinkRequest(), JsonOptions);
        var link = await ReadDataAsync<ConsolePublicLinkToken>(minted, HttpStatusCode.OK);

        using var resolved = await anonymousB.GetAsync($"/api/v1/console/share/link/{link.Token}");
        (await ReadDataAsync<ConsolePublicLinkResolutionResponse>(resolved, HttpStatusCode.OK)).ItemId.Should().Be(item.Id);

        using (var revoked = await adminA.DeleteAsync($"/api/v1/console/content/{item.Id}/share/link/{link.TokenId}"))
        {
            revoked.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var refused = await anonymousB.GetAsync($"/api/v1/console/share/link/{link.Token}");
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound, "a link revoked on one task is refused on the other");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/content")]
    [Endpoint("GET /api/v1/console/content/{id}")]
    [Endpoint("GET /api/v1/console/session")]
    public async Task ContentCreatedOnTaskA_IsServedByTaskB_AndTheSessionReportsSharedDurableState()
    {
        using var adminA = Admin(_taskA);
        using var adminB = Admin(_taskB);
        var item = await CreateItemAsync(adminA);

        using var served = await adminB.GetAsync($"/api/v1/console/content/{item.Id}");
        (await ReadDataAsync<ConsoleContentItem>(served, HttpStatusCode.OK)).Title.Should().Be("Two-task item");

        using var session = await adminB.GetAsync("/api/v1/console/session");
        var persistence = (await ReadDataAsync<ConsoleSessionContext>(session, HttpStatusCode.OK)).Persistence;
        persistence.Mode.Should().Be(ConsolePersistenceNotice.PostgresMode);
        persistence.Durable.Should().BeTrue();
        persistence.SharedAcrossReplicas.Should().BeTrue();
    }

    private static WebAppFixture NewTaskHost(DirectoryInfo keyRing)
        => new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            })
            .ConfigureServices(services => services.AddDataProtection().PersistKeysToFileSystem(keyRing).SetApplicationName("Honua.Server"));

    private static HttpClient Admin(WebAppFixture host)
        => host.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

    private static async Task<ConsoleContentItem> CreateItemAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync("/api/v1/console/content", new CreateConsoleContentItemRequest
        {
            Name = "two-task-" + Guid.NewGuid().ToString("N"),
            ItemType = ConsoleContentItemType.Layer,
            Title = "Two-task item",
            Visibility = ConsoleVisibility.Personal,
        }, JsonOptions);
        return await ReadDataAsync<ConsoleContentItem>(response, HttpStatusCode.Created);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(body, JsonOptions);
        envelope!.Success.Should().BeTrue(body);
        return envelope.Data ?? throw new InvalidOperationException("The response carried no data: " + body);
    }
}
