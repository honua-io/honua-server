// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.SignalR;

namespace Honua.Server.Features.Admin;

internal static class AdminRealtimeHubExtensions
{
    // Without Redis the realtime groups are not advertised and the console polls, because a single replica cannot guarantee cross-replica delivery.
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
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .RequireAdminAuthorization();
}

internal static class AdminRealtimeContract
{
    internal const string HubPath = "/hubs/admin";
    internal const string Protocol = "signalr";
    internal const string StatusChangedEventName = "AdminStatusChanged";

    internal const string ProposalsGroup = "proposals";

    internal const string ProposalPendingEventName = "ProposalPending";

    internal const string ProposalResolvedEventName = "ProposalResolved";

    internal const string OpsHealthGroup = "ops-health";

    internal const string OpsHealthSnapshotEventName = "OpsHealthSnapshot";

    internal const string OperateEventsGroup = "operate-events";

    internal const string OperateEventEventName = "OperateEvent";

    internal const string DeployOperationsGroup = "deploy-operations";

    internal const string DeployOperationEventName = "DeployOperationTransition";
}

// Without a Redis backplane the ops-health, operate-events, and deploy-operations groups are neither advertised nor joinable.
internal sealed class AdminRealtimeCapabilities(bool backplaneEnabled)
{
    public bool BackplaneEnabled { get; } = backplaneEnabled;

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

    public Task SubscribeToProposals()
        => Groups.AddToGroupAsync(Context.ConnectionId, AdminRealtimeContract.ProposalsGroup);

    public Task UnsubscribeFromProposals()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminRealtimeContract.ProposalsGroup);

    public Task SubscribeToOpsHealth()
        => JoinRealtimeGroupAsync(AdminRealtimeContract.OpsHealthGroup);

    public Task UnsubscribeFromOpsHealth()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminRealtimeContract.OpsHealthGroup);

    public Task SubscribeToOperateEvents()
        => JoinRealtimeGroupAsync(AdminRealtimeContract.OperateEventsGroup);

    public Task UnsubscribeFromOperateEvents()
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminRealtimeContract.OperateEventsGroup);

    public Task SubscribeToDeployOperations()
        => JoinRealtimeGroupAsync(AdminRealtimeContract.DeployOperationsGroup);

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

// The proposal event carries only dashboard fields and never the execution payload.
internal sealed record ProposalRealtimeEvent(
    string ProposalId,
    string Kind,
    string Status,
    string? RequestedBy,
    string RiskLevel,
    DateTimeOffset GeneratedAt);

// The deploy transition carries only identifiers and lifecycle classification, and delivery is at-least-once so the client deduplicates it.
internal sealed record DeployOperationRealtimeEvent(
    string EventId,
    string OperationId,
    string TransitionKind,
    string Status,
    string? Phase,
    string? TargetId,
    string? ReleaseId,
    string? CorrelationId,
    string? OperationInstanceId,
    string? AuditId,
    string? ProposalId,
    DateTimeOffset OccurredAt);

[JsonSerializable(typeof(AdminRealtimeStatus))]
[JsonSerializable(typeof(ProposalRealtimeEvent))]
internal sealed partial class AdminRealtimeJsonContext : JsonSerializerContext;

// Kept separate from the status and proposal context so these payloads serialize like their REST counterparts.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpsHealthSnapshotResponse))]
[JsonSerializable(typeof(OperateEventResponse))]
[JsonSerializable(typeof(DeployOperationRealtimeEvent))]
internal sealed partial class AdminRealtimeGroupsJsonContext : JsonSerializerContext;
