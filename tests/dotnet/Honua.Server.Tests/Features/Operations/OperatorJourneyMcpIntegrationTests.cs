// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// An admin finishes the closed operator roster through MCP. A caller without the admin grant
/// is permission_denied and creates no connection row.
/// </summary>
/// <remarks>
/// Lane A and Lane B executors compose only when a durable proposal store is already registered.
/// Entitled Redis is what makes <c>Program</c> register that store before
/// <c>AddOperationsToolset</c>, which is the same host the operator journey runs against.
/// </remarks>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Mcp)]
public sealed class OperatorJourneyMcpIntegrationTests(RedisFixture redis)
{
    private const string SecretVariable = "HONUA_OPERATOR_ROSTER_PG";
    private const string SourceUrl = "https://s3.amazonaws.com/sample-bucket/parcels.geojson";

    private const string GeoJson = """
        {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"Parcel"},"geometry":{"type":"Point","coordinates":[-157.8583,21.3069]}}]}
        """;

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /mcp")]
    public async Task AdminKey_ConfiguresThroughMcp_AndNonAdminCreatesNoRow()
    {
        var fixture = new WebAppFixture()
            .UseKestrel()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting(
                    "Security:RequestSecretReferences:AllowedEnvironmentVariables:0",
                    SecretVariable);
            })
            .ConfigureServices(services =>
            {
                services.AddHttpClient("import-source")
                    .AddHttpMessageHandler(() => new StaticGeoJsonHandler());
            });
        var previousSecret = Environment.GetEnvironmentVariable(SecretVariable);
        await fixture.InitializeAsync();
        try
        {
            Environment.SetEnvironmentVariable(SecretVariable, fixture.Postgres.ConnectionString);
            using var admin = fixture.CreateAdminClient();
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var deniedName = $"denied{suffix}";

            using var createdKey = await admin.PostAsJsonAsync(
                "/api/v1/admin/api-keys",
                new { name = $"reader-{suffix}", permissions = new[] { "read:layers" } });
            var createdKeyBody = await createdKey.Content.ReadAsStringAsync();
            createdKey.StatusCode.Should().Be(HttpStatusCode.Created, createdKeyBody);
            using var createdKeyDocument = JsonDocument.Parse(createdKeyBody);
            var readerKey = createdKeyDocument.RootElement.GetProperty("data").GetProperty("key").GetString()!;
            using var reader = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", readerKey));

            var denied = await CallAsync(reader, "honua_admin_connections_create", new
            {
                name = deniedName,
                host = "127.0.0.1",
                port = 5432,
                databaseName = "honua",
                username = "honua",
                secretReference = $"env:{SecretVariable}",
                secretType = "env",
                sslRequired = false,
                sslMode = "Disable",
            });
            DeniedCode(denied).Should().Be("permission_denied", denied.GetRawText());
            denied.GetRawText().Should().NotContain("RequiresApproval");
            (await ConnectionNamesAsync(admin)).Should().NotContain(deniedName);

            var builder = new NpgsqlConnectionStringBuilder(fixture.Postgres.ConnectionString);
            var connectionName = $"roster{suffix}";
            var created = await CallOkAsync(admin, "honua_admin_connections_create", new
            {
                name = connectionName,
                host = builder.Host,
                port = builder.Port,
                databaseName = builder.Database,
                username = builder.Username,
                secretReference = $"env:{SecretVariable}",
                secretType = "env",
                sslRequired = false,
                sslMode = "Disable",
            });
            var connectionBody = ResponseJson(created);
            var connectionId = connectionBody.RootElement.GetProperty("data").GetProperty("connectionId").GetString();
            connectionId.Should().NotBeNullOrWhiteSpace();

            var tested = await CallOkAsync(admin, "honua_admin_connections_test", new { id = connectionId });
            ResponseJson(tested).RootElement.GetProperty("data").GetProperty("connectionId").GetString()
                .Should().Be(connectionId);

            var tableName = $"roster{suffix}";
            var imported = await CallOkAsync(admin, "honua_admin_import_upload_url", new
            {
                sourceUrl = SourceUrl,
                fileName = "parcels.geojson",
                tableName,
                sourceSrid = 4326,
                targetSrid = 4326,
                overwriteExisting = true,
                forceBackground = false,
            });
            using var importBody = ResponseJson(imported);
            importBody.RootElement.TryGetProperty("success", out var success).Should().BeTrue(importBody.RootElement.GetRawText());
            success.GetBoolean().Should().BeTrue(importBody.RootElement.GetRawText());
            var schema = importBody.RootElement.TryGetProperty("schema", out var schemaValue)
                && schemaValue.ValueKind == JsonValueKind.String
                ? schemaValue.GetString()
                : "public";
            var table = importBody.RootElement.TryGetProperty("physicalTableName", out var physical)
                && physical.ValueKind == JsonValueKind.String
                ? physical.GetString()
                : tableName;

            var serviceName = $"roster{suffix}";
            var published = await CallOkAsync(admin, "honua_admin_layer_publish", new
            {
                connectionId,
                schema,
                table,
                layerName = "Parcels",
                serviceName,
            });
            ResponseJson(published).RootElement.GetProperty("data").GetProperty("layerId").GetInt32()
                .Should().BeGreaterThan(0);

            var policy = await CallOkAsync(admin, "honua_admin_services_access_policy_set", new
            {
                serviceName,
                allowAnonymous = true,
                allowAnonymousWrite = false,
            });
            var policyBody = ResponseJson(policy);
            policyBody.RootElement.GetProperty("data").GetProperty("serviceName").GetString().Should().Be(serviceName);
            policyBody.RootElement.GetProperty("data").GetProperty("accessPolicy").GetProperty("allowAnonymous")
                .GetBoolean().Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretVariable, previousSecret);
            await fixture.DisposeAsync();
        }
    }

    private static async Task<JsonElement> CallOkAsync(HttpClient client, string name, object arguments)
    {
        var root = await CallAsync(client, name, arguments);
        root.TryGetProperty("error", out var error).Should().BeFalse(root.GetRawText());
        var result = root.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            throw new Xunit.Sdk.XunitException(result.GetRawText());
        }

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("requiresApproval").GetBoolean().Should().BeFalse(structured.GetRawText());
        structured.GetProperty("status").GetString().Should().Be("Completed", structured.GetRawText());
        return structured.Clone();
    }

    private static async Task<JsonElement> CallAsync(HttpClient client, string name, object arguments)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name, arguments },
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mcp", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static JsonDocument ResponseJson(JsonElement structured)
    {
        var response = structured.GetProperty("details").GetProperty("response").GetString();
        response.Should().NotBeNullOrWhiteSpace(structured.GetRawText());
        return JsonDocument.Parse(response!);
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

    private static async Task<string[]> ConnectionNamesAsync(HttpClient admin)
    {
        using var response = await admin.GetAsync("/api/v1/admin/connections");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Select(connection => connection.GetProperty("name").GetString()!)
            .ToArray();
    }

    private sealed class StaticGeoJsonHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GeoJson, Encoding.UTF8, "application/geo+json"),
            };
            return Task.FromResult(response);
        }
    }
}
