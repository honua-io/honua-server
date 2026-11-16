// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Extensions;
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
        var fixture = new WebAppFixture().WithProductionJobSubstrate()
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
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var connectionName = $"roster{suffix}";
        var serviceName = $"roster{suffix}";
        await fixture.InitializeAsync();
        try
        {
            Environment.SetEnvironmentVariable(SecretVariable, fixture.Postgres.ConnectionString);
            using var admin = fixture.CreateAdminClient();
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
            var inlineName = $"inline{suffix}";
            var inlineSecret = await CallAsync(admin, "honua_admin_connections_create", new
            {
                name = inlineName,
                host = builder.Host,
                port = builder.Port,
                databaseName = builder.Database,
                username = builder.Username,
                password = "inline-value-must-not-be-echoed",
                secretReference = $"env:{SecretVariable}",
                secretType = "env",
                sslRequired = false,
                sslMode = "Disable",
            });
            DeniedCode(inlineSecret).Should().Be("invalid_argument", inlineSecret.GetRawText());
            inlineSecret.GetRawText().Should().NotContain("inline-value-must-not-be-echoed");
            (await ConnectionNamesAsync(admin)).Should().NotContain(inlineName);

            // MCP callers are agents: each change is a proposal one person approves, and the
            // approval replays under the proposer's managed key, not the bootstrap admin key.
            using var agent = await HumanApprovedOperation.CreateProposerClientAsync(fixture);
            var created = await CallApprovedAsync(fixture, agent, "honua_admin_connections_create", new
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
            using var connectionBody = ResponseJson(created);
            var connectionId = connectionBody.RootElement.GetProperty("data").GetProperty("connectionId").GetString();
            connectionId.Should().NotBeNullOrWhiteSpace();

            var tested = await CallOkAsync(admin, "honua_admin_connections_test", new { id = connectionId });
            using var testBody = ResponseJson(tested);
            testBody.RootElement.GetProperty("data").GetProperty("connectionId").GetString()
                .Should().Be(connectionId);

            var tableName = $"roster{suffix}";
            var imported = await CallApprovedAsync(fixture, agent, "honua_admin_import_upload_url", new
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

            await AssertPublishInspectionAsync(admin, (connectionId!, schema!, table!));

            // MCP callers are agents: publishing and access-policy changes file a proposal for
            // one human approval instead of executing, even with an administrator key.
            await CallProposalAsync(admin, "honua_admin_layer_publish", new
            {
                connectionId,
                schema,
                table,
                layerName = "Parcels",
                serviceName,
            });
            await CallProposalAsync(admin, "honua_admin_services_access_policy_set", new
            {
                serviceName,
                allowAnonymous = true,
                allowAnonymousWrite = false,
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretVariable, previousSecret);
            try
            {
                // Publishing also writes the global compatibility catalog, outside the
                // fixture schema. Remove this journey's service before the next seed
                // deletes connections, otherwise its connection FK blocks initialization.
                await fixture.Postgres.ApplyGlobalSeedSqlAsync(
                    """
                    DELETE FROM honua.services WHERE service_name = @serviceName;
                    DELETE FROM honua.data_connections WHERE name = @connectionName;
                    """,
                    command =>
                    {
                        command.Parameters.AddWithValue("serviceName", serviceName);
                        command.Parameters.AddWithValue("connectionName", connectionName);
                    });
            }
            finally
            {
                await fixture.DisposeAsync();
            }
        }
    }

    // The read-only Publish inspection tools run end to end through tools/call: a multipart file
    // preview, a URL preview through the import source, and a dry-run table validation by route id.
    private static async Task AssertPublishInspectionAsync(
        HttpClient admin, (string ConnectionId, string Schema, string Table) source)
    {
        var (connectionId, schema, table) = source;
        var filePreview = await CallOkAsync(admin, "honua_admin_import_preview", new
        {
            file = Convert.ToBase64String(Encoding.UTF8.GetBytes(GeoJson)),
            fileName = "parcels.geojson",
        });
        using var filePreviewBody = ResponseJson(filePreview);
        filePreviewBody.RootElement.GetRawText().Should().Contain("Parcel");

        var urlPreview = await CallOkAsync(admin, "honua_admin_import_preview_url", new
        {
            sourceUrl = SourceUrl,
            fileName = "parcels.geojson",
        });
        using var urlPreviewBody = ResponseJson(urlPreview);
        urlPreviewBody.RootElement.GetRawText().Should().Contain("Parcel");

        var validated = await CallOkAsync(admin, "honua_admin_connections_tables_validate", new
        {
            id = connectionId,
            schema,
            table,
            layerName = "Parcels",
        });
        using var validatedBody = ResponseJson(validated);
        validatedBody.RootElement.GetProperty("data").GetProperty("table").GetString().Should().Be(table);
    }

    private static async Task CallProposalAsync(HttpClient client, string name, object arguments)
    {
        var root = await CallAsync(client, name, arguments);
        var structured = root.GetProperty("result").GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("RequiresApproval", structured.GetRawText());
        structured.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace(structured.GetRawText());
    }

    private static async Task<JsonElement> CallApprovedAsync(
        WebAppFixture fixture, HttpClient agent, string name, object arguments)
    {
        var root = await CallAsync(agent, name, arguments);
        var structured = root.GetProperty("result").GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("RequiresApproval", structured.GetRawText());
        var proposalId = structured.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace(structured.GetRawText());
        var handle = await HumanApprovedOperation.ApproveAndReadHandleAsync(fixture, proposalId!);
        handle.GetProperty("status").GetString().Should().Be("Completed", handle.GetRawText());
        return handle;
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
        // A direct MCP result carries details at the top; an approved handle nests them under result.
        var status = structured.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? result
            : structured;
        var response = status.GetProperty("details").GetProperty("response").GetString();
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
            // HttpClient transfers ownership of the returned response to the import caller.
            // codeql[cs/local-not-disposed]: HttpClient disposes the response after the caller reads it
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GeoJson, Encoding.UTF8, "application/geo+json"),
            });
    }
}
