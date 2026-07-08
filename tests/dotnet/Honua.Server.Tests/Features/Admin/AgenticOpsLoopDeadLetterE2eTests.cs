// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// End-to-end coverage for the agentic ops loop that proposes and approves the deterministic
/// alert-dispatch dead-letter redrive action (#2568).
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class AgenticOpsLoopDeadLetterE2eTests(RedisFixture redis) : IAsyncLifetime
{
    private const int SeededDeadLetters = 2;
    private const string FindingRule = "alert-dispatch-backlog";
    private const string RedriveAction = "alerts.redrive_dead_letters";

    private readonly StoreBackedAlertDispatchHealth _dispatchHealth = new();
    private WebAppFixture? _fixture;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                // Host settings are required because Program decides whether Redis-backed
                // durable control-plane services are wired before ConfigureAppConfiguration runs.
                builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAlertDispatchHealth>();
                services.AddSingleton(_dispatchHealth);
                services.AddSingleton<IAlertDispatchHealth>(
                    sp => sp.GetRequiredService<StoreBackedAlertDispatchHealth>());
            });

        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

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

        var repeatedProposal = await ProposeFindingAsync(client, findingId!);
        repeatedProposal.GetProperty("proposalId").GetString().Should().Be(
            proposalId,
            "re-proposing the same live finding must fold onto the same idempotent gateway proposal");

        var approved = await ApproveProposalAsync(client, proposalId!);
        approved.GetProperty("status").GetString().Should().Be("Submitted");
        approved.GetProperty("requestedByAgent").GetString().Should().Be("ops-findings");
        approved.GetProperty("resolvedBy").GetString().Should().Be("admin");

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

        await AssertAuditRowAsync(fixture, "operation.proposed", proposalId!);
        await AssertAuditRowAsync(fixture, "operation.applied", proposalId!);
        await AssertAuditRowAsync(fixture, RedriveAction, changeId!);

        var proposalTimeline = await ReadTimelineAsync(client, proposalId!);
        proposalTimeline.Should().Contain(item => item.GetProperty("title").GetString() == "operation.proposed");
        proposalTimeline.Should().Contain(item => item.GetProperty("title").GetString() == "operation.applied");

        var actionTimeline = await ReadTimelineAsync(client, changeId!);
        var redriveEvent = actionTimeline.Should().ContainSingle(
            item => item.GetProperty("title").GetString() == RedriveAction).Subject;
        redriveEvent.GetProperty("actor").GetString().Should().Be("system:ops-action");
        redriveEvent.GetProperty("resourceRef").GetString().Should().Be("alert-dispatch/dead-letters");
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
            await dispatchStore.EnqueueAsync(eventId.Value, ImmutableArray.Create(AlertChannelType.WebSocket));
        }

        var claimed = await dispatchStore.ClaimPendingAsync(count, DateTimeOffset.UtcNow.AddMinutes(1));
        claimed.Select(item => item.EventId).Should().BeEquivalentTo(
            eventIds,
            "the rows enqueued for the seeded fault must be the rows marked dead-lettered");

        foreach (var item in claimed)
        {
            await dispatchStore.MarkFailedAsync(
                item.DispatchId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                deadLetter: true,
                errorMessage: "seeded dead-letter storm for #2568");
        }
    }

    private async Task RefreshCachedBacklogAsync(IAlertDispatchStore store)
        => _dispatchHealth.SetBacklog(await store.GetBacklogAsync());

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

    private static async Task<JsonElement> ApproveProposalAsync(HttpClient client, string proposalId)
    {
        using var response = await client.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);
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
        var dataSource = fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.audit_log WHERE action = @action AND correlation_id = @correlation_id",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("correlation_id", correlationId);

        var count = await command.ExecuteScalarAsync();
        count.Should().Be(1L);
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

        public AlertDispatchBacklog? LastBacklog { get; private set; }

        public bool IsStoragePollFailing => false;

        public void SetBacklog(AlertDispatchBacklog backlog)
        {
            LastBacklog = backlog;
            LastPollAt = DateTimeOffset.UtcNow;
        }
    }
}
