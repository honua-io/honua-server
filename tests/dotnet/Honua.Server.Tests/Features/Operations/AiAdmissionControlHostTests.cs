// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Security.Admission;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Update)]
public sealed class AiAdmissionControlHostTests
{
    private const string Password = "Synthetic-AiAdmission-6587!";
    private const string Deployment = "admission-control-test";

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task AdminPostgresRedisOff_ControlsUseDurableLedgerAndAudit()
    {
        var fixture = await CreateFixtureAsync("Allow");
        try
        {
            UnavailableOperationProposalStore.IsDurable(fixture.Services.GetService<IOperationProposalStore>()).Should().BeFalse();
            using var admin = Admin(fixture);
            foreach (var action in new[] { "initialize", "resume", "stop", "policy-update" })
            {
                var parameters = new Dictionary<string, string?>();
                if (action is "initialize" or "policy-update")
                    parameters["policy"] = JsonSerializer.Serialize(new AiAdmissionPolicy(), AiAdmissionControlJsonContext.Default.AiAdmissionPolicy);
                var response = await SubmitAsync(admin, action, parameters);
                response.Status.Should().Be(HttpStatusCode.OK);
                response.Handle.GetProperty("status").GetString().Should().Be("Completed");
                response.Handle.GetProperty("auditId").GetString().Should().NotBeNullOrWhiteSpace();
                response.Handle.GetProperty("evidenceRefs").EnumerateArray().Select(item => item.GetString())
                    .Should().Contain(NonDurableDirectOperationInstanceStore.NonDurableEvidenceRef);
            }
            using var scope = fixture.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>();
            var state = await store.GetStateAsync(Deployment, CancellationToken.None);
            state.Should().NotBeNull();
            state!.Stopped.Should().BeTrue();
            state.Revision.Should().Be(2);
            (await CountControlEventsAsync(fixture)).Should().Be(4);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task AnonymousAndReadOnlyAdminKey_CannotMutateAdmission()
    {
        var fixture = await CreateFixtureAsync("Allow");
        try
        {
            using var anonymous = fixture.CreateClient();
            using var response = await PostControlAsync(anonymous, "initialize", PolicyParameters());
            response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
            using var admin = Admin(fixture);
            using var create = await admin.PostAsync("/api/v1/admin/api-keys", JsonContent("""{"name":"admission-reader","permissions":["admin:read"]}"""));
            create.StatusCode.Should().Be(HttpStatusCode.Created);
            using var body = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var key = body.RootElement.GetProperty("data").GetProperty("key").GetString()!;
            using var reader = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
            using var denied = await PostControlAsync(reader, "initialize", PolicyParameters());
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await CountControlEventsAsync(fixture)).Should().Be(0);
            using var scope = fixture.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<IAiExecutionAdmissionStore>()
                .GetStateAsync(Deployment, CancellationToken.None)).Should().BeNull();
        }
        finally { await fixture.DisposeAsync(); }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task RequireApprovalWithoutRedis_DoesNotFallBackToDirectMutation()
    {
        var fixture = await CreateFixtureAsync("RequireApproval");
        try
        {
            using var admin = Admin(fixture);
            var response = await SubmitAsync(admin, "initialize", PolicyParameters());
            response.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await CountControlEventsAsync(fixture)).Should().Be(0);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task WrongDeploymentAndStaleExpectedTuple_LeaveLedgerUnchanged()
    {
        var fixture = await CreateFixtureAsync("Allow");
        try
        {
            using var admin = Admin(fixture);
            var initialized = await SubmitAsync(admin, "initialize", PolicyParameters());
            initialized.Handle.GetProperty("status").GetString().Should().Be("Completed");
            var wrong = await SubmitAsync(admin, "resume", new Dictionary<string, string?> { ["deploymentId"] = "another-deployment" });
            wrong.Handle.GetProperty("status").GetString().Should().Be("Failed");
            var details = initialized.Handle.GetProperty("result").GetProperty("details");
            var stale = await SubmitAsync(admin, "resume", new Dictionary<string, string?>
            {
                ["expectedGeneration"] = details.GetProperty("generation").GetString(),
                ["expectedRevision"] = "99",
                ["expectedEpoch"] = "1"
            });
            stale.Handle.GetProperty("status").GetString().Should().Be("Denied");
            stale.Handle.GetProperty("reason").GetString().Should().Be("stale_control");
            (await CountControlEventsAsync(fixture)).Should().Be(1);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static async Task<WebAppFixture> CreateFixtureAsync(string decision)
    {
        var fixture = new WebAppFixture();
        fixture.UseEnvironment(Environments.Production).ConfigureWebHost(builder =>
        {
            builder.UseSetting("Licensing:Mode", "Disabled");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", Password);
            builder.UseSetting("Operations:Policy:Enabled", "true");
            builder.UseSetting("Operations:Policy:BuiltInProductionRules", "false");
            builder.UseSetting("Operations:Policy:DefaultDecision", "Deny");
            builder.UseSetting("Operations:Policy:Rules:0:Role", "admin");
            builder.UseSetting("Operations:Policy:Rules:0:Decision", decision);
        }).ConfigureServices(services =>
        {
            services.Configure<AiExecutionAdmissionOptions>(options => options.DeploymentId = Deployment);
            services.AddScoped<IAiExecutionAdmissionStore>(provider => new PostgresAiExecutionAdmissionStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), fixture.CurrentSchema));
        });
        try
        {
            await fixture.InitializeAsync();
            // Apply the exact additive migration to this fixture schema even when the shared
            // test template predates this component. No manually mirrored test table contract.
            var assembly = typeof(AiAdmissionControlExecutor).Assembly;
            var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("140_CreateAiExecutionAdmission.sql", StringComparison.Ordinal));
            await using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var sql = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{fixture.CurrentSchema}\"", StringComparison.Ordinal);
            await using var connection = await fixture.Postgres.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private static HttpClient Admin(WebAppFixture fixture)
        => fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", Password));

    private static Dictionary<string, string?> PolicyParameters() => new()
    { ["policy"] = JsonSerializer.Serialize(new AiAdmissionPolicy(), AiAdmissionControlJsonContext.Default.AiAdmissionPolicy) };

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<(HttpStatusCode Status, JsonElement Handle)> SubmitAsync(HttpClient client, string action, Dictionary<string, string?> parameters)
    {
        using var response = await PostControlAsync(client, action, parameters);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var handle = body.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
        return (response.StatusCode, handle);
    }

    private static async Task<HttpResponseMessage> PostControlAsync(HttpClient client, string action, Dictionary<string, string?> parameters)
    {
        using var content = JsonContent(JsonSerializer.Serialize(new { parameters }));
        return await client.PostAsync("/api/v1/operations/admin.ai.admission." + action + "/submit", content);
    }

    private static async Task<long> CountControlEventsAsync(WebAppFixture fixture)
    {
        await using var connection = await fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{fixture.CurrentSchema}\".ai_execution_control_events", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
