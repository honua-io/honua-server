// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.SignalR;

namespace Honua.Server.Features.Admin;

internal static class AdminRealtimeHubExtensions
{
    /// <summary>
    /// Registers the admin realtime hub, its background broadcaster, and — when a Redis connection is
    /// configured — the SignalR Redis backplane so leased-replica producers (the deploy reconciler, the
    /// ops-health sampler) reach clients connected to any replica (#2554).
    /// </summary>
    /// <remarks>
    /// Backplane feature-detect contract: when no Redis connection string is configured the hub runs
    /// single-replica only, so the realtime groups are NOT advertised (see
    /// <see cref="AdminRealtimeStatus.RealtimeGroups"/> / <see cref="AdminRealtimeStatus.BackplaneEnabled"/>)
    /// and the console falls back to polling the ops-health / history / list APIs. When Redis is present the
    /// groups are advertised and pushed. A Redis outage at runtime never blocks serving: the backplane
    /// connection is created with <c>AbortOnConnectFail=false</c>, sends run on an isolated background loop,
    /// and clients re-sync via the history/list APIs (there is no Last-Event-ID replay).
    /// </remarks>
    public static IServiceCollection AddAdminRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var redisConnectionString = configuration.GetConnectionString("redis")
            ?? configuration["Aspire:StackExchange:Redis:ConnectionString"];
        var backplaneEnabled = !string.IsNullOrWhiteSpace(redisConnectionString);

        services.AddSingleton(new AdminRealtimeCapabilities(backplaneEnabled));

        var signalR = services
            .AddSignalR(options =>
            {
                // Bounded per-connection behavior: a client that stops reading past the transport buffer is
                // disconnected rather than buffered unboundedly, and a stalled client is timed out. These are
                // the framework's protective defaults, pinned explicitly so the realtime groups cannot become
                // an unbounded per-connection memory sink under a slow consumer.
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.MaximumParallelInvocationsPerClient = 1;
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(
                    0,
                    AdminRealtimeGroupsJsonContext.Default);
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(
                    0,
                    AdminRealtimeJsonContext.Default);
            });

        if (backplaneEnabled)
        {
            // Dedicated backplane connection. AbortOnConnectFail=false so a Redis outage degrades to
            // single-replica delivery (fail-open) instead of faulting hub startup or serving.
            signalR.AddStackExchangeRedis(redisConnectionString!, options =>
            {
                options.Configuration.AbortOnConnectFail = false;
                options.Configuration.ChannelPrefix =
                    StackExchange.Redis.RedisChannel.Literal("honua:signalr:admin");
            });
        }

        // Background broadcaster: subscribes to the ops-health flush signal and receives forwarded workflow
        // transitions, fanning both out to the hub groups on an isolated loop that never blocks the sampler
        // or the store-write path.
        services.AddSingleton<AdminRealtimeBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<AdminRealtimeBroadcaster>());

        // Second, independent consumer of the #2562 workflow-transition seam that forwards transitions to the
        // deploy-operations / operate-events groups. Registered as an additional listener so the transition
        // store decorator fans transitions to it alongside the operate-timeline producer.
        services.AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IWorkflowOperationTransitionListener,
            RealtimeOperationTransitionListener>();

        return services;
    }

    public static IEndpointConventionBuilder MapAdminRealtimeHub(this IEndpointRouteBuilder endpoints)
        => endpoints.MapHub<AdminHub>(AdminRealtimeContract.HubPath)
            .WithDisplayName("Admin realtime hub")
            .RequireAdminAuthorization();
}

internal static class AdminRealtimeContract
{
    internal const string HubPath = "/hubs/admin";
    internal const string Protocol = "signalr";
    internal const string StatusChangedEventName = "AdminStatusChanged";

    /// <summary>SignalR group reviewers join to receive operation-proposal events.</summary>
    internal const string ProposalsGroup = "proposals";

    /// <summary>Event raised when a new proposal is pending human approval.</summary>
    internal const string ProposalPendingEventName = "ProposalPending";

    /// <summary>Event raised when a proposal is resolved (approved/rejected/terminal).</summary>
    internal const string ProposalResolvedEventName = "ProposalResolved";

    /// <summary>
    /// Group carrying cluster-aggregated ops-health snapshots (#2554). Pushed the SAME DTO as
    /// <c>GET ops-health</c> on every sampler flush; a client backfills a reconnect gap by re-fetching the
    /// history API (no Last-Event-ID replay).
    /// </summary>
    internal const string OpsHealthGroup = "ops-health";

    /// <summary>Event name for an ops-health snapshot push.</summary>
    internal const string OpsHealthSnapshotEventName = "OpsHealthSnapshot";

    /// <summary>Group carrying new Operate-timeline events (currently deploy/release transitions, #2562).</summary>
    internal const string OperateEventsGroup = "operate-events";

    /// <summary>Event name for an operate-timeline event push.</summary>
    internal const string OperateEventEventName = "OperateEvent";

    /// <summary>Group carrying deploy/workflow-operation transitions for the deploy cockpit (#2554).</summary>
    internal const string DeployOperationsGroup = "deploy-operations";

    /// <summary>
    /// Event name for a deploy-operation transition push. Delivery is at-least-once (the #2562 seam classifies
    /// absolute status, not deltas), so the payload carries a stable <c>eventId</c> plus
    /// <c>operationId</c>+<c>transitionKind</c> for client-side dedup.
    /// </summary>
    internal const string DeployOperationEventName = "DeployOperationTransition";
}

/// <summary>
/// Process-wide realtime capabilities resolved at startup. Drives what the hub advertises and which group
/// subscriptions are accepted: without a Redis backplane, cross-replica fan-out cannot be guaranteed, so the
/// ops-health / operate-events / deploy-operations groups are neither advertised nor joinable and the console
/// polls instead.
/// </summary>
internal sealed class AdminRealtimeCapabilities(bool backplaneEnabled)
{
    /// <summary>Gets a value indicating whether the Redis backplane is active (multi-replica fan-out).</summary>
    public bool BackplaneEnabled { get; } = backplaneEnabled;

    /// <summary>The realtime groups advertised to clients; empty when the backplane is absent.</summary>
    public IReadOnlyList<string> AdvertisedGroups { get; } = backplaneEnabled
        ?
        [
            AdminRealtimeContract.OpsHealthGroup,
            AdminRealtimeContract.OperateEventsGroup,
            AdminRealtimeContract.DeployOperationsGroup
        ]
        : Array.Empty<string>();
}

internal sealed class AdminHub(
    IHostEnvironment environment,
    MigrationState migrationState,
    AdminRealtimeCapabilities capabilities) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync(
            AdminRealtimeContract.StatusChangedEventName,
            CreateStatus(),
            Context.ConnectionAborted).ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public Task<AdminRealtimeStatus> GetStatus()
        => Task.FromResult(CreateStatus());

    /// <summary>Joins the proposals group so the caller receives proposal events.</summary>
    public Task SubscribeToProposals()
        => Groups.AddToGroupAsync(Context.ConnectionId, AdminRealtimeContract.ProposalsGroup);

    /// <summary>Leaves the proposals group.</summary>
    public Task UnsubscribeFromProposals()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminRealtimeContract.ProposalsGroup);

    /// <summary>Joins the ops-health group so the caller receives cluster-aggregated snapshot pushes.</summary>
    public Task SubscribeToOpsHealth()
        => JoinRealtimeGroupAsync(AdminRealtimeContract.OpsHealthGroup);

    /// <summary>Leaves the ops-health group.</summary>
    public Task UnsubscribeFromOpsHealth()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminRealtimeContract.OpsHealthGroup);

    /// <summary>Joins the operate-events group so the caller receives new Operate-timeline events.</summary>
    public Task SubscribeToOperateEvents()
        => JoinRealtimeGroupAsync(AdminRealtimeContract.OperateEventsGroup);

    /// <summary>Leaves the operate-events group.</summary>
    public Task UnsubscribeFromOperateEvents()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminRealtimeContract.OperateEventsGroup);

    /// <summary>Joins the deploy-operations group so the caller receives workflow-operation transitions.</summary>
    public Task SubscribeToDeployOperations()
        => JoinRealtimeGroupAsync(AdminRealtimeContract.DeployOperationsGroup);

    /// <summary>Leaves the deploy-operations group.</summary>
    public Task UnsubscribeFromDeployOperations()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminRealtimeContract.DeployOperationsGroup);

    private Task JoinRealtimeGroupAsync(string group)
    {
        // Feature-detect contract: refuse subscriptions to groups that are not advertised (no backplane) so a
        // client cannot silently join a group that will never deliver cross-replica — it must fall back to
        // polling instead.
        if (!capabilities.BackplaneEnabled)
        {
            throw new HubException(
                $"Realtime group '{group}' is unavailable because the Redis backplane is not configured. " +
                "Poll the ops-health / history / deploy-operations APIs instead.");
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    private AdminRealtimeStatus CreateStatus()
        => new(
            Status: migrationState.IsFailed ? "degraded" : "online",
            InstanceId: Environment.MachineName,
            EnvironmentName: environment.EnvironmentName,
            MigrationStatus: GetMigrationStatusLabel(migrationState.Status),
            MigrationReady: migrationState.IsReady,
            GeneratedAt: DateTimeOffset.UtcNow,
            BackplaneEnabled: capabilities.BackplaneEnabled,
            RealtimeGroups: capabilities.AdvertisedGroups);

    private static string GetMigrationStatusLabel(MigrationLifecycleStatus status)
        => status switch
        {
            MigrationLifecycleStatus.Running => "running",
            MigrationLifecycleStatus.Succeeded => "succeeded",
            MigrationLifecycleStatus.Skipped => "skipped",
            MigrationLifecycleStatus.Failed => "failed",
            _ => "unknown"
        };
}

internal sealed record AdminRealtimeStatus(
    string Status,
    string InstanceId,
    string EnvironmentName,
    string MigrationStatus,
    bool MigrationReady,
    DateTimeOffset GeneratedAt,
    bool BackplaneEnabled,
    IReadOnlyList<string> RealtimeGroups);

/// <summary>
/// Lightweight proposal event payload pushed to the proposals group. Carries only
/// fields the dashboard needs (id, kind, status, requester, risk) — never the full
/// execution payload.
/// </summary>
internal sealed record ProposalRealtimeEvent(
    string ProposalId,
    string Kind,
    string Status,
    string? RequestedBy,
    string RiskLevel,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Lightweight deploy/workflow-operation transition pushed to the <c>deploy-operations</c> group. Carries
/// only identifiers and lifecycle classification — never the full operation record or provider payload.
/// Delivery is at-least-once; <see cref="EventId"/> (and the <see cref="OperationId"/>+<see cref="TransitionKind"/>
/// pair) are the client dedup keys.
/// </summary>
internal sealed record DeployOperationRealtimeEvent(
    string EventId,
    string OperationId,
    string TransitionKind,
    string Status,
    string? Phase,
    string? TargetId,
    string? ReleaseId,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

[JsonSerializable(typeof(AdminRealtimeStatus))]
[JsonSerializable(typeof(ProposalRealtimeEvent))]
internal sealed partial class AdminRealtimeJsonContext : JsonSerializerContext;

/// <summary>
/// Camel-cased, null-omitting source-generated context for the realtime group payloads. Kept separate from
/// <see cref="AdminRealtimeJsonContext"/> so the ops-health snapshot and operate-event pushes serialize
/// byte-identically to their REST counterparts (same casing + <c>WhenWritingNull</c> omission) without
/// changing the existing status/proposal wire shapes.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpsHealthSnapshotResponse))]
[JsonSerializable(typeof(OperateEventResponse))]
[JsonSerializable(typeof(DeployOperationRealtimeEvent))]
internal sealed partial class AdminRealtimeGroupsJsonContext : JsonSerializerContext;
