// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed partial class McpConnectionDiscoveryIntegrationTests : IAsyncLifetime
{
    private readonly RedisFixture _redis = new();

    public Task InitializeAsync() => _redis.InitializeAsync();
    public Task DisposeAsync() => _redis.DisposeAsync();

    private const string SecretVariable = "HONUA_MCP_DISCOVERY_PG";
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /mcp")]
    public async Task ConnectionDiscovery_AdminAndNonAdminKeys_MatchRestPermissionsAndReturnOnlySafeFields()
    {
        await using var fixture = CreateConnectionDiscoveryFixture();
        await fixture.InitializeAsync();
        var previousSecret = Environment.GetEnvironmentVariable(SecretVariable);
        var name = $"discovery{Guid.NewGuid():N}"[..17];
        try
        {
            Environment.SetEnvironmentVariable(SecretVariable, fixture.Postgres.ConnectionString);
            using var admin = fixture.CreateAdminClient();
            (await ListToolNamesAsync(admin, "full")).Should().Contain("honua_admin_connection_layers_list");
            var data = await CreateDiscoveryDataAsync(fixture, admin, name);
            foreach (var view in new[] { "configure", "setup", "full" })
                (await ListToolNamesAsync(admin, view)).Should().Contain(
                    ["honua_admin_connections_list", "honua_admin_connection_tables_list"], view);
            var context = new DiscoveryContext(fixture, admin, data);
            foreach (var grant in DiscoveryGrants) await AssertDiscoveryGrantAsync(context, grant);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretVariable, previousSecret);
            await fixture.Postgres.ApplyGlobalSeedSqlAsync($"DROP TABLE IF EXISTS public.{name}; DELETE FROM honua.services WHERE service_name = @name; DELETE FROM honua.data_connections WHERE name = @name;",
                command => command.Parameters.AddWithValue("name", name));
        }
    }

    private sealed record DiscoveryData(string ConnectionId, string Name, int StorageLayerId);
    private sealed record DiscoveryContext(WebAppFixture Fixture, HttpClient Admin, DiscoveryData Data);
    private sealed record DiscoveryGrant(string Permission, HttpStatusCode Status);
    private sealed record DiscoveryAccess(DiscoveryContext Context, HttpClient Client, DiscoveryGrant Grant);
    private sealed record DiscoveryRead(string Name, string Path, object Arguments);
    private static readonly DiscoveryGrant[] DiscoveryGrants =
    [
        new("admin:*", HttpStatusCode.OK), new("admin:read", HttpStatusCode.OK), new("admin:write", HttpStatusCode.OK),
        new("read:layers", HttpStatusCode.Forbidden), new("ops:read", HttpStatusCode.Forbidden)
    ];

    private WebAppFixture CreateConnectionDiscoveryFixture() =>
        new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", _redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Security:RequestSecretReferences:AllowedEnvironmentVariables:0", SecretVariable);
        });

    private static async Task<DiscoveryData> CreateDiscoveryDataAsync(WebAppFixture fixture, HttpClient admin, string name)
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.Postgres.ConnectionString);
        using var created = await admin.PostAsJsonAsync("/api/v1/admin/connections", new
        {
            name,
            host = connection.Host,
            port = connection.Port,
            databaseName = connection.Database,
            username = connection.Username,
            secretReference = $"env:{SecretVariable}",
            secretType = "env",
            sslRequired = false,
            sslMode = "Disable",
            description = "private-description-must-not-be-serialized"
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("data").GetProperty("connectionId").GetString()!;
        await fixture.Postgres.ApplyGlobalSeedSqlAsync(
            $"CREATE TABLE public.{name} (id integer, geom geometry(Point,4326)); INSERT INTO public.{name} VALUES (1, ST_SetSRID(ST_MakePoint(1,2),4326)); ANALYZE public.{name};");
        var storageLayerId = await PublishDiscoveryLayerAsync(admin, id, name);
        return new DiscoveryData(id, name, storageLayerId);
    }

    private static async Task<int> PublishDiscoveryLayerAsync(HttpClient admin, string id, string name)
    {
        using var published = await admin.PostAsJsonAsync($"/api/v1/admin/connections/{id}/layers", new
        {
            schema = "public",
            table = name,
            layerName = name,
            serviceName = name,
            geometryColumn = "geom",
            geometryType = "POINT",
            srid = 4326
        });
        published.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("data").GetProperty("layerId").GetInt32();
    }

    private static async Task AssertDiscoveryGrantAsync(DiscoveryContext context, DiscoveryGrant grant)
    {
        using var keyResponse = await context.Admin.PostAsJsonAsync("/api/v1/admin/api-keys", new
        {
            name = $"discovery-{grant.Permission}-{context.Data.Name}",
            permissions = new[] { grant.Permission }
        });
        keyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var keyBody = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
        var key = keyBody.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        using var client = context.Fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", key));
        var access = new DiscoveryAccess(context, client, grant);
        foreach (var read in DiscoveryReads(context.Data)) await AssertDiscoveryReadAsync(access, read);
    }

    private static DiscoveryRead[] DiscoveryReads(DiscoveryData data) =>
    [
        new("honua_admin_connections_list", "/api/v1/admin/connections", new { }),
        new("honua_admin_connection_tables_list", $"/api/v1/admin/connections/{data.ConnectionId}/tables", new { connectionId = data.ConnectionId }),
        new("honua_admin_connection_layers_list", $"/api/v1/admin/connections/{data.ConnectionId}/layers?serviceName={data.Name}",
            new { connectionId = data.ConnectionId, serviceName = data.Name })
    ];

    private static async Task AssertDiscoveryReadAsync(DiscoveryAccess access, DiscoveryRead read)
    {
        using var rest = await access.Client.GetAsync(read.Path);
        rest.StatusCode.Should().Be(access.Grant.Status, access.Grant.Permission);
        var call = await CallAsync(access.Client, new ToolInvocation(read.Name, read.Arguments, "full"));
        if (access.Grant.Status == HttpStatusCode.Forbidden)
        {
            DeniedCode(call).Should().Be("permission_denied", $"{access.Grant.Permission}: {call.GetRawText()}");
            return;
        }
        call.TryGetProperty("error", out _).Should().BeFalse(call.GetRawText());
        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeFalse(call.GetRawText());
        var payload = call.GetProperty("result").GetProperty("structuredContent");
        switch (read.Name)
        {
            case "honua_admin_connections_list": AssertConnectionRow(payload, access.Context.Data); break;
            case "honua_admin_connection_tables_list": AssertTableRow(payload, access.Context.Data); break;
            case "honua_admin_connection_layers_list": AssertStorageLayerRow(payload, access.Context.Data); break;
            default: throw new InvalidOperationException();
        }
        AssertNoConnectionSecrets(call, access.Context.Fixture);
    }

    private static void AssertConnectionRow(JsonElement payload, DiscoveryData data)
    {
        var row = payload.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("id").GetString() == data.ConnectionId);
        row.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("id", "name", "provider", "status");
        row.GetProperty("name").GetString().Should().Be(data.Name);
        row.GetProperty("provider").GetString().Should().Be("postgis");
        row.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static void AssertTableRow(JsonElement payload, DiscoveryData data)
    {
        var row = payload.GetProperty("tables").EnumerateArray().Single(item => item.GetProperty("table").GetString() == data.Name);
        row.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("schema", "table", "geometryColumn", "geometryType", "estimatedRows");
        row.GetProperty("schema").GetString().Should().Be("public");
        row.GetProperty("geometryColumn").GetString().Should().Be("geom");
        row.GetProperty("geometryType").GetString().Should().Be("POINT");
        row.GetProperty("estimatedRows").GetInt64().Should().Be(1);
    }

    private static void AssertStorageLayerRow(JsonElement payload, DiscoveryData data)
    {
        var row = payload.GetProperty("layers").EnumerateArray().Single(item => item.GetProperty("layerId").GetInt32() == data.StorageLayerId);
        row.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("layerId", "layerName", "schema", "table", "serviceName", "enabled");
        row.GetProperty("serviceName").GetString().Should().Be(data.Name);
        payload.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    private static void AssertNoConnectionSecrets(JsonElement call, WebAppFixture fixture)
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.Postgres.ConnectionString);
        call.GetRawText().Should().NotContain(fixture.Postgres.ConnectionString)
            .And.NotContain(connection.Password!).And.NotContain("private-description-must-not-be-serialized")
            .And.NotContain(SecretVariable).And.NotContain("credentialReference").And.NotContain("connectionString")
            .And.NotContain("encryptedConnectionString").And.NotContain("username");
    }

    private static async Task<List<string>> ListToolNamesAsync(HttpClient client, string view)
    {
        var names = new List<string>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            using var response = await client.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list",
                @params = new { view, cursor },
            });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = document.RootElement.GetProperty("result");
            names.AddRange(result.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString()!));
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (cursor is null)
            {
                return names;
            }

            cursors.Add(cursor).Should().BeTrue($"{view} must not repeat a pagination cursor");
        }

        cursor.Should().BeNull($"{view} discovery must finish within 100 pages");
        return names;
    }

    private sealed record ToolInvocation(string Name, object Arguments, string? View = null);

    private static Task<JsonElement> CallAsync(HttpClient client, string name, object arguments) =>
        CallAsync(client, new ToolInvocation(name, arguments));

    private static async Task<JsonElement> CallAsync(HttpClient client, ToolInvocation invocation)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = invocation.Name,
                arguments = invocation.Arguments,
                _meta = new Dictionary<string, string?> { ["honua.io/workflow-view"] = invocation.View }
            },
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mcp", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string? DeniedCode(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error)
            && error.TryGetProperty("data", out var data)
            && data.TryGetProperty("code", out var code))
        {
            return code.GetString();
        }

        if (root.TryGetProperty("result", out var result)
            && result.TryGetProperty("structuredContent", out var structured)
            && structured.TryGetProperty("code", out var structuredCode))
        {
            return structuredCode.GetString();
        }

        return null;
    }
}
