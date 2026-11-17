// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Console;

// The Console keeps Catalog, Share and Open Data behind a "not available yet" gate until the manifest
// advertises console.content.persistent as supported and available. The row follows the store the
// host selected: PostgreSQL with a system database, in memory without one. Both composition paths are
// covered because the hand-curated roster and the registry projection must stay wire-identical.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class ConsoleContentCapabilityManifestTests
{
    private const string CapabilityId = "console.content.persistent";

    [IntegrationTheory]
    [InlineData(ManifestPath.Curated, ConsoleStore.Postgres, null)]
    [InlineData(ManifestPath.Registry, ConsoleStore.Postgres, null)]
    [InlineData(ManifestPath.Curated, ConsoleStore.InMemory, "dependency-unavailable")]
    [InlineData(ManifestPath.Registry, ConsoleStore.InMemory, "dependency-unavailable")]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_PersistentContent_FollowsSelectedStore(ManifestPath path, ConsoleStore store, string? expectedReason)
    {
        await using var fixture = CreateFixture(path, store);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();

        using var document = await GetManifestAsync(client);
        var row = Capability(document, CapabilityId);

        row.GetProperty("category").GetString().Should().Be("console");
        row.GetProperty("lifecycle").GetString().Should().Be("implemented");
        row.GetProperty("supported").GetBoolean().Should().BeTrue("the PostgreSQL Console stores ship in every build");
        row.GetProperty("available").GetBoolean().Should().Be(store == ConsoleStore.Postgres);
        ReasonCode(row).Should().Be(expectedReason);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_PersistentContent_IsAvailableToAnonymousReaders()
    {
        // Public share pages read the manifest before anyone signs in.
        await using var fixture = CreateFixture(ManifestPath.Curated, ConsoleStore.Postgres);
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();

        using var document = await GetManifestAsync(client);

        Capability(document, CapabilityId).GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData(ConsoleStore.Postgres)]
    [InlineData(ConsoleStore.InMemory)]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_CompositionPaths_AgreeOnPersistentContent(ConsoleStore store)
    {
        await using var curatedFixture = CreateFixture(ManifestPath.Curated, store);
        await curatedFixture.InitializeAsync();
        using var curatedClient = curatedFixture.CreateAdminClient();
        using var curated = await GetManifestAsync(curatedClient);

        await using var derivedFixture = CreateFixture(ManifestPath.Registry, store);
        await derivedFixture.InitializeAsync();
        using var derivedClient = derivedFixture.CreateAdminClient();
        using var derived = await GetManifestAsync(derivedClient);

        Capability(derived, CapabilityId).GetRawText().Should().Be(Capability(curated, CapabilityId).GetRawText());
    }

    private static string? ReasonCode(JsonElement row)
        => row.TryGetProperty("reasonCode", out var reason) && reason.ValueKind != JsonValueKind.Null
            ? reason.GetString()
            : null;

    private static async Task<JsonDocument> GetManifestAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Capability(JsonDocument manifest, string id)
    {
        var match = manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Where(capability => capability.GetProperty("id").GetString() == id)
            .ToArray();
        match.Should().ContainSingle($"the manifest must publish exactly one '{id}' row");
        return match[0];
    }

    // The fixture host has a system database, so it selects the PostgreSQL stores; the in-memory shape replaces
    // the notice those registrations publish, which is the value the manifest reads.
    private static WebAppFixture CreateFixture(ManifestPath path, ConsoleStore store)
    {
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
        if (store == ConsoleStore.InMemory)
        {
            fixture = fixture.ConfigureServices(services =>
            {
                services.RemoveAll<ConsolePersistenceNotice>();
                services.AddSingleton(ConsolePersistenceNotice.InMemory);
            });
        }

        return fixture.ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Capabilities:ManifestFromRegistry"] = path == ManifestPath.Registry ? "true" : "false",
                });
            });
        });
    }

    public enum ManifestPath
    {
        Curated,
        Registry,
    }

    public enum ConsoleStore
    {
        Postgres,
        InMemory,
    }
}
