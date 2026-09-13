// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using IOperationInstanceStore = Honua.Core.Features.Operations.Abstractions.IOperationInstanceStore;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// #3361 acceptance: a destructive access operation persists its canonical proposal payload and
/// idempotency identity, and after the host restarts over the same Redis and PostgreSQL stores the
/// operation instance, proposal, audit and correlation identities still line up through generic clients.
/// </summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class AdminAccessProposalRestartTests(RedisFixture redis)
{
    private const string BootstrapKey = "lane-c-restart-bootstrap-key";
    private const string DestructiveOperationId = "admin.role.delete";

    [IntegrationTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("GET /api/v1/operations/handles/{handleId}")]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    [Endpoint("GET /api/v1/admin/observability/audit")]
    public async Task DestructiveAccessOperation_ProposalIdentitiesAndIdempotencySurviveHostRestart()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", BootstrapKey);

                // Entitled Redis makes the server compose its production approval surface: the Redis
                // proposal store and the operation gateway. Each host generation connects its own
                // multiplexer to the same Redis.
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");

                // Production requires an enabled policy; this rule routes the destructive operation to approval.
                builder.UseSetting("Operations:Policy:Enabled", "true");
                builder.UseSetting("Operations:Policy:Rules:0:OperationId", DestructiveOperationId);
                builder.UseSetting("Operations:Policy:Rules:0:Decision", "RequireApproval");
                builder.UseSetting("Operations:Policy:Rules:0:ApprovalLane", "operator-gate");
            })
            .ConfigureServices(services =>
            {
                // The Test environment composes a volatile instance store; the restart proof binds the
                // production Redis instance store, so the recreated service container reads the instance
                // and proposal records the first host wrote.
                services.RemoveAll<IOperationInstanceStore>();
                services.AddSingleton<IOperationInstanceStore>(sp =>
                    new RedisOperationInstanceStore(sp.GetRequiredService<IConnectionMultiplexer>()));
            });
        await fixture.InitializeAsync();
        try
        {
            var roleId = Guid.NewGuid().ToString();
            var idempotencyKey = $"lane-c-restart-{Guid.NewGuid():N}";
            JsonElement accepted;
            using (var client = AdminClient(fixture))
            {
                accepted = await SubmitAsync(client, roleId, idempotencyKey);
            }

            accepted.GetProperty("status").GetString().Should().Be("RequiresApproval", accepted.GetRawText());
            var operationInstanceId = RequiredId(accepted, "operationInstanceId");
            var proposalId = RequiredId(accepted, "proposalId");
            var auditId = RequiredId(accepted, "auditId");
            var correlationId = RequiredId(accepted, "correlationId");
            new[] { operationInstanceId, proposalId, auditId, correlationId }.Should().OnlyHaveUniqueItems();

            var proposal = await fixture.GetService<IOperationProposalStore>().GetAsync(proposalId);
            proposal.Should().NotBeNull();
            proposal!.OperationId.Should().Be(DestructiveOperationId);
            proposal.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
            proposal.Audit.OperationInstanceId.Should().Be(operationInstanceId);
            proposal.Audit.AuditId.Should().Be(auditId);
            proposal.Audit.CorrelationId.Should().Be(correlationId);
            proposal.Audit.IdempotencyKey.Should().NotBeNullOrWhiteSpace()
                .And.NotBe(idempotencyKey, "the durable key is scoped to the caller's tenant and principal");
            proposal.Plan.ExecutionPayload.Should().Contain(roleId);

            await fixture.RestartHostAsync();
            using var restarted = AdminClient(fixture);

            using (var status = await restarted.GetAsync($"/api/v1/operations/handles/{operationInstanceId}"))
            {
                var body = await status.Content.ReadAsStringAsync();
                status.StatusCode.Should().Be(HttpStatusCode.OK, body);
                using var document = JsonDocument.Parse(body);
                var data = document.RootElement.GetProperty("data");
                data.GetProperty("status").GetString().Should().Be("RequiresApproval");
                data.GetProperty("operationInstanceId").GetString().Should().Be(operationInstanceId);
                data.GetProperty("proposalId").GetString().Should().Be(proposalId);
                data.GetProperty("auditId").GetString().Should().Be(auditId);
                data.GetProperty("correlationId").GetString().Should().Be(correlationId);
            }

            using (var read = await restarted.GetAsync($"/api/v1/admin/proposals/{proposalId}"))
            {
                var body = await read.Content.ReadAsStringAsync();
                read.StatusCode.Should().Be(HttpStatusCode.OK, body);
                using var document = JsonDocument.Parse(body);
                document.RootElement.GetProperty("proposalId").GetString().Should().Be(proposalId);
                document.RootElement.GetProperty("status").GetString().Should().Be("AwaitingApproval");
            }

            using (var audit = await restarted.GetAsync(
                       $"/api/v1/admin/observability/audit?correlationId={Uri.EscapeDataString(correlationId)}"))
            {
                var body = await audit.Content.ReadAsStringAsync();
                audit.StatusCode.Should().Be(HttpStatusCode.OK, body);
                using var document = JsonDocument.Parse(body);
                // The audit page returns the sink-assigned row id as a number; the handle carries it as text.
                document.RootElement.GetProperty("items").EnumerateArray()
                    .Select(static item => item.GetProperty("auditId").GetRawText())
                    .Should().Contain(auditId);
            }

            // A retry with the same key after the restart folds onto the same invocation and proposal.
            var retried = await SubmitAsync(restarted, roleId, idempotencyKey);
            retried.GetProperty("status").GetString().Should().Be("RequiresApproval", retried.GetRawText());
            retried.GetProperty("operationInstanceId").GetString().Should().Be(operationInstanceId);
            retried.GetProperty("proposalId").GetString().Should().Be(proposalId);
            retried.GetProperty("correlationId").GetString().Should().Be(correlationId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static HttpClient AdminClient(WebAppFixture fixture)
        => fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", BootstrapKey));

    private static async Task<JsonElement> SubmitAsync(HttpClient client, string roleId, string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operations/{DestructiveOperationId}/submit")
        {
            Content = JsonContent.Create(new { parameters = new Dictionary<string, string?> { ["id"] = roleId } }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static string RequiredId(JsonElement handle, string name)
    {
        var value = handle.TryGetProperty(name, out var element) ? element.GetString() : null;
        value.Should().NotBeNullOrWhiteSpace("the handle must expose '{0}': {1}", name, handle.GetRawText());
        return value!;
    }
}
