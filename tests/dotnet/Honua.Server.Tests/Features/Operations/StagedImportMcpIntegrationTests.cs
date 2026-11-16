// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

// An agent previews a completed job artifact over MCP, proposes its staged import, and a person
// approves it. Nothing is imported before approval. A caller without import authority is refused.
// The job service is the only substitute: it returns a completed job whose artifact is inline
// GeoJSON. Authorization, the dispatcher, the approval gateway, the import ledger and the
// PostGIS import all run in the real host.
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StagedImportMcpIntegrationTests
{
    private const string JobId = "gp-staged-import";

    private const string GeoJson = """
        {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"Parcel"},"geometry":{"type":"Point","coordinates":[-157.8583,21.3069]}}]}
        """;

    [IntegrationTest]
    [Operation(Operations.Import)]
    [Endpoint("POST /mcp")]
    public async Task AgentProposal_ImportsOnlyAfterHumanApproval_AndNonAdminIsRefused()
    {
        var artifact = FeatureCollectionArtifact.BuildDataUri(Encoding.UTF8.GetBytes(GeoJson));
        var fixture = CreateFixture(artifact);
        await fixture.InitializeAsync();
        try
        {
            await EnsureImportReceiptTableAsync(fixture);
            var schema = fixture.CurrentSchema ?? "public";
            // Approval replays under the proposer's stored API-key authority, so the agent uses one.
            using var agent = await HumanApprovedOperation.CreateProposerClientAsync(fixture);
            var (proposalId, stagingTable) = await PreviewAndProposeAsync(fixture, agent, Arguments(schema, artifact));

            var handle = await HumanApprovedOperation.ApproveAndReadHandleAsync(fixture, proposalId);
            handle.GetProperty("status").GetString().Should().Be("Completed", handle.GetRawText());
            var physicalTable = handle.GetProperty("result").GetProperty("details").GetProperty("physicalTable").GetString()!;
            physicalTable.Should().Be("imported_" + stagingTable, "the import lands in the previewed server-owned table");
            (await RowCountAsync(fixture, schema, physicalTable)).Should().Be(1, handle.GetRawText());

            using var admin = fixture.CreateAdminClient();
            using var reader = await ReaderClientAsync(fixture, admin);
            var denied = await CallAsync(reader, "honua_op_dataset_stage_preview", Arguments(schema, artifact, "intent-denied"));
            denied.GetRawText().Should().Contain("permission_denied");
            denied.GetRawText().Should().NotContain("previewFingerprint");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(string artifact)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync(JobId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(CompletedJob(artifact));
        return new WebAppFixture().WithProductionJobSubstrate()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Public:BaseUrl", "http://localhost");
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton(jobs);
            });
    }

    private static async Task<(string ProposalId, string StagingTable)> PreviewAndProposeAsync(
        WebAppFixture fixture, HttpClient agent, Dictionary<string, object?> source)
    {
        var schema = (string)source["schema"]!;
        var preview = await CallCompletedAsync(agent, "honua_op_dataset_stage_preview", source);
        var details = preview.GetProperty("details");
        var stagingTable = details.GetProperty("stagingTable").GetString()!;
        stagingTable.Should().StartWith("staged_");
        details.GetProperty("featureCount").GetString().Should().Be("1");

        var proposalId = await CallProposalAsync(agent, "honua_op_dataset_stage_import", new Dictionary<string, object?>(source)
        {
            ["previewFingerprint"] = details.GetProperty("previewFingerprint").GetString(),
        });
        (await RowCountAsync(fixture, schema, stagingTable)).Should().BeNull("a proposal must not import");
        (await RowCountAsync(fixture, schema, "imported_" + stagingTable)).Should().BeNull("a proposal must not import");
        return (proposalId, stagingTable);
    }

    // The shared test database skips migrations; apply the shipped receipt migration the
    // durable import ledger needs (idempotent: CREATE ... IF NOT EXISTS).
    private static async Task EnsureImportReceiptTableAsync(WebAppFixture fixture)
    {
        var script = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
            "src", "Honua.Server", "Migrations", "138_CreateJobArtifactImportReceipt.sql"));
        await using var connection = new NpgsqlConnection(fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        var metadataSchema = fixture.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Database:Schema"] ?? "honua";
        await using var command = new NpgsqlCommand(
            script.Replace("$HonuaSchema$", $"\"{metadataSchema}\"", StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }

    private static Dictionary<string, object?> Arguments(string schema, string artifact, string intentId = "intent-staged")
        => new(StringComparer.Ordinal)
        {
            ["intentId"] = intentId,
            ["schema"] = schema,
            ["targetId"] = "target-local",
            ["jobId"] = JobId,
            ["resultPackageId"] = $"{JobId}:v2",
            ["artifactId"] = $"{JobId}:artifact:1",
            ["referenceSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(artifact))),
            ["targetSrid"] = 4326,
            ["sourceSrid"] = 4326,
        };

    private static ExecutionJobRecord CompletedJob(string artifact) => new()
    {
        OperationId = JobId,
        Status = ExecutionJobStatus.Succeeded,
        Version = 2,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        ArtifactReferences = [artifact],
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "geoprocessing:test",
            Parameters = new Dictionary<string, string>(),
        },
    };

    private static async Task<HttpClient> ReaderClientAsync(WebAppFixture fixture, HttpClient admin)
    {
        using var created = await admin.PostAsJsonAsync(
            "/api/v1/admin/api-keys", new { name = $"reader-{Guid.NewGuid():N}", permissions = new[] { "read:layers" } });
        var body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var key = document.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        return fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
    }

    private static async Task<long?> RowCountAsync(WebAppFixture fixture, string schema, string table)
    {
        await using var connection = new NpgsqlConnection(fixture.Postgres.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@relation) IS NOT NULL", connection);
        exists.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        if (await exists.ExecuteScalarAsync() is not true)
        {
            return null;
        }

        await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{table}\"", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task<string> CallProposalAsync(HttpClient client, string name, object arguments)
    {
        var root = await CallAsync(client, name, arguments);
        var structured = root.GetProperty("result").GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("RequiresApproval", structured.GetRawText());
        var proposalId = structured.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace(structured.GetRawText());
        return proposalId!;
    }

    private static async Task<JsonElement> CallCompletedAsync(HttpClient client, string name, object arguments)
    {
        var root = await CallAsync(client, name, arguments);
        root.TryGetProperty("error", out _).Should().BeFalse(root.GetRawText());
        var result = root.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            throw new Xunit.Sdk.XunitException(result.GetRawText());
        }

        var structured = result.GetProperty("structuredContent");
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
}
