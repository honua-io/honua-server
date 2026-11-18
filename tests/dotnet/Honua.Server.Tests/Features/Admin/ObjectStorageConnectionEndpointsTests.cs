// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ObjectStorageConnectionEndpointsTests(ObjectStorageConnectionEndpointsTests.Host host)
    : IClassFixture<ObjectStorageConnectionEndpointsTests.Host>
{
    private const string AdminPassword = "object-storage-connection-admin-key";
    private const string RoleArn = "arn:aws:iam::123456789012:role/honua-connection-imports";

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/object-storage/setup")]
    public async Task Setup_MintsADistinctExternalIdEachTime()
    {
        using var client = await host.ClientAsync();

        var first = await DataAsync(await client.PostAsync("/api/v1/admin/connections/object-storage/setup?bucket=customer-imports&prefix=parcels", content: null));
        var second = await DataAsync(await client.PostAsync("/api/v1/admin/connections/object-storage/setup?bucket=customer-imports&prefix=parcels", content: null));

        first.GetProperty("externalId").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        second.GetProperty("externalId").GetString().Should().NotBe(first.GetProperty("externalId").GetString());
        first.GetProperty("roleNamePrefix").GetString().Should().Be("honua-connection-");
        first.TryGetProperty("quickCreateUrl", out _).Should().BeFalse("no template URL is configured");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/object-storage/setup")]
    public async Task Setup_IsNotAvailableOverGet()
    {
        using var client = await host.ClientAsync();

        using var response = await client.GetAsync("/api/v1/admin/connections/object-storage/setup?bucket=customer-imports&prefix=parcels");

        response.StatusCode.Should().BeOneOf([HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound], "minting stores state, so it is never a GET");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/object-storage/setup")]
    public async Task Setup_RefusesAnInvalidBucketOrPrefix()
    {
        using var client = await host.ClientAsync();

        using var badBucket = await client.PostAsync("/api/v1/admin/connections/object-storage/setup?bucket=192.168.0.1", content: null);
        using var badPrefix = await client.PostAsync("/api/v1/admin/connections/object-storage/setup?bucket=customer-imports&prefix=../x", content: null);

        badBucket.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        badPrefix.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    [Endpoint("GET /api/v1/admin/connections")]
    [Endpoint("GET /api/v1/admin/connections/{id}")]
    public async Task Create_WithAMintedExternalId_ListsAndReadsTheConnection()
    {
        using var client = await host.ClientAsync();
        var externalId = await MintAsync(client);

        using var created = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(externalId));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await DataAsync(created)).GetProperty("id").GetGuid();

        var listed = await DataAsync(await client.GetAsync("/api/v1/admin/connections"));
        var row = listed.EnumerateArray().Single(item => item.GetProperty("connectionId").GetGuid() == id);
        row.GetProperty("kind").GetString().Should().Be("object-storage");
        row.GetProperty("provider").GetString().Should().Be("aws-s3");
        row.GetProperty("host").GetString().Should().BeEmpty();
        row.GetProperty("prefix").GetString().Should().Be("parcels/");
        row.ToString().Should().NotContain(externalId);
        var single = await DataAsync(await client.GetAsync($"/api/v1/admin/connections/{id}"));
        single.GetProperty("bucket").GetString().Should().Be("customer-imports");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task Create_RefusesUnknownAndReusedExternalIds()
    {
        using var client = await host.ClientAsync();
        var externalId = await MintAsync(client);

        using var unknown = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(new string('a', 64)));
        using var first = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(externalId));
        using var reused = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(externalId));

        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        reused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await reused.Content.ReadAsStringAsync()).Should().Contain("already used");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections")]
    public async Task Create_RefusesRolesRegionsAndBucketsOutsideThePolicy()
    {
        using var client = await host.ClientAsync();
        var externalId = await MintAsync(client);

        using var role = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(externalId) with { RoleArn = "arn:aws:iam::123456789012:role/admin" });
        using var region = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(externalId) with { Region = "evil.example" });
        using var bucket = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(externalId) with { Bucket = "xn--bucket" });

        role.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        region.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        bucket.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/connections/{id}")]
    [Endpoint("DELETE /api/v1/admin/connections/{id}")]
    public async Task Update_KeepsTheExternalIdAndDeleteRemovesTheConnection()
    {
        using var client = await host.ClientAsync();
        var id = await CreateAsync(client);

        using var rebind = await client.PutAsJsonAsync($"/api/v1/admin/connections/{id}", new { externalId = new string('b', 64) });
        using var move = await client.PutAsJsonAsync($"/api/v1/admin/connections/{id}", new { prefix = "other/" });
        using var rename = await client.PutAsJsonAsync($"/api/v1/admin/connections/{id}", new { name = "Renamed imports" });
        using var deleted = await client.DeleteAsync($"/api/v1/admin/connections/{id}");
        using var gone = await client.GetAsync($"/api/v1/admin/connections/{id}");

        rebind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        move.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        rename.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(rename)).GetProperty("name").GetString().Should().Be("Renamed imports");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/connections/test")]
    public async Task DraftTest_RefusesAnExternalIdThisServerDidNotMint()
    {
        using var client = await host.ClientAsync();

        using var response = await client.PostAsJsonAsync("/api/v1/admin/connections/test", Create(new string('c', 64)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/connections/{id}/objects")]
    public async Task Browse_RefusesUnknownConnectionsAndPrefixesOutsideTheConnection()
    {
        using var client = await host.ClientAsync();
        var id = await CreateAsync(client);

        using var unknown = await client.GetAsync($"/api/v1/admin/connections/{Guid.NewGuid()}/objects");
        using var outside = await client.GetAsync($"/api/v1/admin/connections/{id}/objects?prefix=other/");
        using var traversal = await client.GetAsync($"/api/v1/admin/connections/{id}/objects?prefix=parcels/../other/");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        outside.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        traversal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static CreateBody Create(string externalId) => new()
    {
        ExternalId = externalId,
    };

    private static async Task<string> MintAsync(HttpClient client)
    {
        var setup = await DataAsync(await client.PostAsync("/api/v1/admin/connections/object-storage/setup?bucket=customer-imports&prefix=parcels/", content: null));
        return setup.GetProperty("externalId").GetString()!;
    }

    private static async Task<Guid> CreateAsync(HttpClient client)
    {
        using var created = await client.PostAsJsonAsync("/api/v1/admin/connections", Create(await MintAsync(client)));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await DataAsync(created)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.IsSuccessStatusCode.Should().BeTrue(body);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("data").Clone();
        }
    }

    private sealed record CreateBody
    {
        public string Kind { get; init; } = "object-storage";

        public string Provider { get; init; } = "aws-s3";

        public string Name { get; init; } = "Customer imports";

        public string Bucket { get; init; } = "customer-imports";

        public string Prefix { get; init; } = "parcels/";

        public string Region { get; init; } = "us-west-2";

        public string RoleArn { get; init; } = ObjectStorageConnectionEndpointsTests.RoleArn;

        public required string ExternalId { get; init; }
    }

    public sealed class Host : IAsyncLifetime
    {
        private readonly WebAppFixture _application = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            });

        private readonly Lazy<Task> _startup;

        public Host()
        {
            _startup = new Lazy<Task>(_application.InitializeAsync);
        }

        public async Task<HttpClient> ClientAsync()
        {
            await _startup.Value;
            return _application.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _application.DisposeAsync();
    }
}
