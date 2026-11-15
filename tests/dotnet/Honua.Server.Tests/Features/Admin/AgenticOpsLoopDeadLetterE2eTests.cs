// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Extensions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// End-to-end coverage for the agentic ops loop that proposes and approves the deterministic
/// alert-dispatch dead-letter redrive action (#2568).
/// </summary>
[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class AgenticOpsLoopDeadLetterE2eTests(RedisFixture redis) : IAsyncLifetime, IClassFixture<RedisFixture>
{
    private const int SeededDeadLetters = 2;
    private const string FindingRule = "alert-dispatch-backlog";
    private const string RedriveAction = "alerts.redrive_dead_letters";

    private readonly StoreBackedAlertDispatchHealth _dispatchHealth = new();
    private WebAppFixture? _fixture;
    private HttpClient? _client;
    private HumanApprovalHttpClient? _human;
    private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identity = AdminAuthEndpointsTests.CreateStubFactory();

    public async Task InitializeAsync()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        _fixture = HumanApprovalHttpClient.Configure(new WebAppFixture().WithProductionJobSubstrate(), _identity)
            .ConfigureWebHost(builder =>
            {
                // Host settings are required because Program decides whether Redis-backed
                // durable control-plane services are wired before ConfigureAppConfiguration runs.
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            })
            .ConfigureServices(services =>
            {
                // The isolated WebAppFixture disables alerts after custom app-configuration
                // callbacks run. Override the bound options at the service boundary so this
                // test proves the enabled ops path rather than silently exercising the no-op.
                services.RemoveAll<IOptions<AlertOptions>>();
                services.AddSingleton<IOptions<AlertOptions>>(Options.Create(new AlertOptions
                {
                    Edition = AlertEdition.Pro,
                    Ops = new AlertOpsOptions
                    {
                        Enabled = true,
                        Channels = ["webhook"],
                    },
                }));
                services.RemoveAll<IAlertDispatchHealth>();
                services.AddSingleton(_dispatchHealth);
                services.AddSingleton<IAlertDispatchHealth>(
                    sp => sp.GetRequiredService<StoreBackedAlertDispatchHealth>());
            });

        await _fixture.InitializeAsync();
        _dispatchHealth.Refresh = cancellationToken => _fixture
            .GetService<IAlertDispatchStore>()
            .GetBacklogAsync(cancellationToken);
        _client = _fixture.CreateAdminClient();
        _human = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity, tenantId: "public");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _human?.Dispose();
        _identity.Dispose();

        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }

        await DeleteControlPlaneKeysAsync(redis.ConnectionString);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/findings")]
    [Endpoint("POST /api/v1/admin/observability/findings/{findingId}/propose")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("GET /api/v1/admin/observability/events")]
    public async Task DeadLetterStorm_ProposeApproveRedrive_ClearsFindingAndAuditsChain()
    {
        var fixture = _fixture!;
        var client = _client!;
        var dispatchStore = fixture.GetService<IAlertDispatchStore>();

        await SeedDeadLetterStormAsync(fixture, SeededDeadLetters);
        await RefreshCachedBacklogAsync(dispatchStore);

        _dispatchHealth.LastBacklog!.DeadLetteredCount.Should().BeGreaterThanOrEqualTo(
            SeededDeadLetters,
            "the seeded fault must exceed the alert-dispatch dead-letter finding threshold");

        var mcpFinding = await ReadFindingThroughMcpAsync(fixture);
        mcpFinding.GetProperty("severity").GetString().Should().Be("Critical");
        mcpFinding.GetProperty("recommendedAction")
            .GetProperty("kind")
            .GetString()
            .Should()
            .Be("AdminConfigChange");
        mcpFinding.GetProperty("recommendedAction")
            .GetProperty("summary")
            .GetString()
            .Should()
            .Contain("dead-lettered alert dispatch");

        var restFinding = await ReadFindingThroughRestAsync(client);
        var findingId = restFinding.GetProperty("id").GetString();
        findingId.Should().NotBeNullOrWhiteSpace();

        var firstProposal = await ProposeFindingAsync(client, findingId!);
        firstProposal.GetProperty("status").GetString().Should().Be("ProposalCreated");
        var proposalId = firstProposal.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace();
        // Ruling 4: proposal audit evidence is joined to the original durable
        // invocation correlation, not a proposal-local fallback identity.
        var proposalStore = fixture.GetService<IOperationProposalStore>();
        var proposalCorrelationId = (await proposalStore.GetAsync(proposalId!))!.Audit.CorrelationId!;
        var proposalAuditCount = await CountAuditRowsAsync(fixture, "operation.proposed", proposalCorrelationId);
        proposalAuditCount.Should().BeGreaterThan(0,
            "the first proposal must emit durable audit evidence");

        var repeatedProposal = await ProposeFindingAsync(client, findingId!);
        repeatedProposal.GetProperty("proposalId").GetString().Should().Be(
            proposalId,
            "re-proposing the same live finding must fold onto the same idempotent gateway proposal");
        (await CountAuditRowsAsync(fixture, "operation.proposed", proposalCorrelationId)).Should().Be(
            proposalAuditCount,
            "an idempotent proposal replay must not append another proposal audit event");

        var approved = await ApproveProposalAsync(proposalId!);
        // Ruling 1: only queued/running work awaits reconciler finalization; the
        // synchronous redrive replay is terminal when approval returns.
        approved.GetProperty("status").GetString().Should().Be("Succeeded", approved.GetRawText());
        approved.GetProperty("requestedByAgent").GetString().Should().Be("ops-findings");
        approved.GetProperty("resolvedBy").GetString().Should().Be(_human!.ActorId);

        var changeId = approved.GetProperty("executionOperationId").GetString();
        changeId.Should().StartWith("opsaction-");

        var postRedriveBacklog = await dispatchStore.GetBacklogAsync();
        postRedriveBacklog.DeadLetteredCount.Should().Be(0);
        postRedriveBacklog.PendingCount.Should().BeGreaterThanOrEqualTo(
            SeededDeadLetters,
            "redrive re-enqueues dead-letter rows for normal delivery rather than dropping them");
        _dispatchHealth.SetBacklog(postRedriveBacklog);

        await ReadFindingThroughRestAsync(client, expectPresent: false);
        (await ReadFindingsThroughMcpAsync(fixture)).Should().BeEmpty();

        await AssertAuditRowAsync(fixture, "operation.applied", proposalCorrelationId);
        await AssertAuditRowAsync(fixture, RedriveAction, changeId!);

        var proposalTimeline = await ReadTimelineAsync(client, proposalCorrelationId);
        proposalTimeline.Should().Contain(item => item.GetProperty("title").GetString() == "operation.proposed");
        proposalTimeline.Should().Contain(item => item.GetProperty("title").GetString() == "operation.applied");

        var actionTimeline = await ReadTimelineAsync(client, changeId!);
        var redriveEvent = actionTimeline.Should().ContainSingle(
            item => item.GetProperty("title").GetString() == RedriveAction).Subject;
        redriveEvent.GetProperty("actor").GetString().Should().Be("system:ops-action");
        redriveEvent.GetProperty("resourceRef").GetString().Should().Be("alert-dispatch/dead-letters");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/findings")]
    public async Task DeadLetterStorm_AutoApplyPolicy_CannotBypassHumanApprovalForUncompensatableAction()
    {
        var fixture = _fixture!;
        var dispatchStore = fixture.GetService<IAlertDispatchStore>();
        var autonomyStore = fixture.GetService<IOpsAutonomyPolicyStore>();
        var initialRedriveAuditCount = await CountAuditRowsAsync(fixture, RedriveAction);
        await autonomyStore.SetSettingsAsync(
            new OpsAutonomySettings { KillSwitchEnabled = false }, "integration-test");
        await autonomyStore.SetPolicyAsync(
            new OpsAutonomyPolicy
            {
                Rule = FindingRule,
                Mode = OpsAutonomyMode.AutoApply,
                MaxAutoActionsPerWindow = 2,
                Window = TimeSpan.FromHours(1),
                MaxBlastRadius = SeededDeadLetters,
            }, "integration-test");

        await SeedDeadLetterStormAsync(fixture, SeededDeadLetters);
        await RefreshCachedBacklogAsync(dispatchStore);
        await using var scope = fixture.Services.CreateAsyncScope();
        var findingsService = scope.ServiceProvider.GetRequiredService<IOpsFindingsService>();
        var finding = (await findingsService.EvaluateAsync())
            .Should().ContainSingle(item => item.Rule == FindingRule).Subject;
        finding.RecommendedAction!.AutoSafe.Should().BeFalse();
        var routeDecision = await scope.ServiceProvider.GetRequiredService<IOpsAutonomyEvaluator>()
            .EvaluateFindingAsync(finding);
        routeDecision.CanAutoApply.Should().BeFalse();

        var result = await findingsService.ProposeAsync(finding.Id);
        result.Status.Should().Be(OpsFindingProposalStatus.ProposalCreated,
            "configuration cannot make delivered notifications compensatable");
        (await dispatchStore.GetBacklogAsync()).DeadLetteredCount.Should().Be(SeededDeadLetters);
        (await CountAuditRowsAsync(fixture, RedriveAction)).Should().Be(initialRedriveAuditCount);
        (await fixture.GetService<IOperationProposalStore>()
            .ListActiveAsync(OperationClass.AdminConfigChange)).Should().ContainSingle();
    }

    private async Task SeedDeadLetterStormAsync(WebAppFixture fixture, int count)
    {
        var eventStore = fixture.GetService<IAlertEventStore>();
        var dispatchStore = fixture.GetService<IAlertDispatchStore>();
        var eventIds = new HashSet<long>();

        for (var index = 0; index < count; index++)
        {
            var eventId = await eventStore.TryAppendAsync(new AlertEventEnvelope
            {
                DedupeKey = $"ops:test-2568-dead-letter:{Guid.NewGuid():N}",
                RuleId = 0,
                ServiceId = "agentic-ops-loop",
                LayerId = 0,
                ObjectId = index + 1,
                TriggerType = AlertTriggerType.Threshold,
                Generation = index + 1,
                Severity = AlertSeverity.Critical,
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                PayloadJson = "{\"test\":\"2568\",\"fault\":\"dead-letter-storm\"}",
                IncidentStatus = AlertIncidentStatus.Started,
                Source = AlertEventSources.Ops,
            });

            eventId.Should().NotBeNull();
            eventIds.Add(eventId!.Value);
            await dispatchStore.EnqueueAsync(eventId!.Value, ImmutableArray.Create(AlertChannelType.WebSocket));
        }

        await MarkSeededDispatchesDeadLetterAsync(fixture, eventIds);
    }

    private static async Task MarkSeededDispatchesDeadLetterAsync(WebAppFixture fixture, HashSet<long> eventIds)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = 0;

        await fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
        {
            await using var connection = await fixture.Postgres.GetConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                UPDATE honua.alert_dispatch
                SET status = @status,
                    attempts = attempts + 1,
                    next_attempt_at = @now,
                    last_attempt_at = @now,
                    last_error = @last_error,
                    updated_at = now()
                WHERE event_id = ANY(@event_ids)
                """,
                connection);
            command.Parameters.AddWithValue("status", (short)AlertDispatchStatus.DeadLetter);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("last_error", "seeded dead-letter storm for #2568");
            command.Parameters.AddWithValue("event_ids", eventIds.ToArray());

            updated = await command.ExecuteNonQueryAsync();
        });

        updated.Should().Be(
            eventIds.Count,
            "only the dispatch rows enqueued for the seeded fault should be marked dead-lettered");
    }

    private async Task RefreshCachedBacklogAsync(IAlertDispatchStore store)
        => _dispatchHealth.SetBacklog(await store.GetBacklogAsync());

    private static async Task DrainPendingDispatchesAsync(IAlertDispatchStore store)
    {
        var pending = await store.ClaimPendingAsync(1_000, DateTimeOffset.UtcNow.AddMinutes(1));
        foreach (var item in pending)
        {
            await store.MarkDeliveredAsync(item.DispatchId, item.ClaimToken, DateTimeOffset.UtcNow);
        }
    }

    private static async Task<JsonElement> ReadFindingThroughMcpAsync(WebAppFixture fixture)
        => (await ReadFindingsThroughMcpAsync(fixture)).Should().ContainSingle().Subject;

    private static async Task<IReadOnlyList<JsonElement>> ReadFindingsThroughMcpAsync(WebAppFixture fixture)
    {
        var reader = fixture.GetService<IMcpOpsObservabilityReader>();
        var result = await reader.GetOpsFindingsAsync(
            CreateAdminPrincipal(),
            new McpOpsFindingsArgument { Rule = FindingRule },
            CancellationToken.None);

        return result.GetProperty("findings").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task<JsonElement> ReadFindingThroughRestAsync(
        HttpClient client,
        bool expectPresent = true)
    {
        using var response = await client.GetAsync("/api/v1/admin/observability/findings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var matches = document.RootElement
            .GetProperty("findings")
            .EnumerateArray()
            .Where(item => item.GetProperty("rule").GetString() == FindingRule)
            .Select(item => item.Clone())
            .ToArray();

        if (!expectPresent)
        {
            matches.Should().BeEmpty();
            return default;
        }

        return matches.Should().ContainSingle().Subject;
    }

    private static async Task<JsonElement> ProposeFindingAsync(HttpClient client, string findingId)
    {
        using var response = await client.PostAsync($"/api/v1/admin/observability/findings/{findingId}/propose", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> ApproveProposalAsync(string proposalId)
    {
        using var response = await _human!.ApproveAsync(proposalId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement[]> ReadTimelineAsync(HttpClient client, string correlationId)
    {
        using var response = await client.GetAsync(
            $"/api/v1/admin/observability/events?correlationId={Uri.EscapeDataString(correlationId)}&pageSize=20");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task AssertAuditRowAsync(WebAppFixture fixture, string action, string correlationId)
    {
        var count = await CountAuditRowsAsync(fixture, action, correlationId);
        count.Should().Be(1L);
    }

    private static async Task<long> CountAuditRowsAsync(
        WebAppFixture fixture,
        string action,
        string correlationId)
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.audit_log WHERE action = @action AND correlation_id = @correlation_id",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("correlation_id", correlationId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadLatestAuditCorrelationAsync(WebAppFixture fixture, string action)
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT correlation_id FROM honua.audit_log WHERE action = @action ORDER BY audit_id DESC LIMIT 1",
            connection);
        command.Parameters.AddWithValue("action", action);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> ReadAuditActionsAsync(WebAppFixture fixture, string correlationId)
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT action FROM honua.audit_log WHERE correlation_id = @correlation_id ORDER BY audit_id",
            connection);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        await using var reader = await command.ExecuteReaderAsync();
        var actions = new List<string>();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetString(0));
        }

        return actions.ToArray();
    }

    private static async Task<long> CountAuditRowsAsync(WebAppFixture fixture, string action)
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.audit_log WHERE action = @action",
            connection);
        command.Parameters.AddWithValue("action", action);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadLatestOpsAutonomyAlertAsync(WebAppFixture fixture)
    {
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM honua.alert_events "
            + "WHERE source = 'ops' AND service_id = 'ops-autonomy' ORDER BY event_id DESC LIMIT 1",
            connection);
        var payload = await command.ExecuteScalarAsync();
        payload.Should().NotBeNull(
            "a successful autonomous action must emit durable operator evidence through the alert outbox");
        return (string)payload!;
    }

    private static ClaimsPrincipal CreateAdminPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "admin"),
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
            ],
            AuthenticationExtensions.ApiKeyScheme));

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        var server = GetServer(multiplexer);
        var keys = server.Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }

    private static IServer GetServer(ConnectionMultiplexer multiplexer)
    {
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException("Redis connection string did not provide any endpoints.");
        }

        return multiplexer.GetServer(endpoints[0]);
    }

    private sealed class StoreBackedAlertDispatchHealth : IAlertDispatchHealth
    {
        public bool IsDispatcherRunning => true;

        public bool IsDispatcherEnabled => true;

        public DateTimeOffset? LastPollAt { get; private set; }

        public DateTimeOffset? BacklogObservedAt => LastPollAt;

        public Honua.Alerts.AlertDispatchObservation? LastObservation =>
            LastBacklog is { } backlog ? new(backlog, BacklogObservedAt) : null;

        public AlertDispatchBacklog? LastBacklog { get; private set; }

        public bool IsStoragePollFailing => false;

        public Func<CancellationToken, Task<AlertDispatchBacklog>>? Refresh { get; set; }

        public async Task<AlertDispatchBacklog> RefreshBacklogAsync(CancellationToken cancellationToken = default)
        {
            var backlog = Refresh is null
                ? LastBacklog ?? new AlertDispatchBacklog { PendingCount = 0, DeadLetteredCount = 0 }
                : await Refresh(cancellationToken);
            SetBacklog(backlog);
            return backlog;
        }

        public void SetBacklog(AlertDispatchBacklog backlog)
        {
            LastBacklog = backlog;
            LastPollAt = DateTimeOffset.UtcNow;
        }
    }
}
