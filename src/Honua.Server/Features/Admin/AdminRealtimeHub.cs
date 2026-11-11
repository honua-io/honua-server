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
    public static IServiceCollection AddAdminRealtime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddSignalR(options =>
            {
                // Bounded per-connection behavior: a client that stops reading past the transport buffer is
                // disconnected rather than buffered unboundedly, and a stalled client is timed out. These are
                // the framework's protective defaults, pinned explicitly so the hub cannot become an unbounded
                // per-connection memory sink under a slow consumer.
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                options.MaximumParallelInvocationsPerClient = 1;
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(
                    0,
                    AdminRealtimeJsonContext.Default);
            });

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
}

internal sealed class AdminHub(
    IHostEnvironment environment,
    MigrationState migrationState) : Hub
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

    private AdminRealtimeStatus CreateStatus()
        => new(
            Status: migrationState.IsFailed ? "degraded" : "online",
            InstanceId: Environment.MachineName,
            EnvironmentName: environment.EnvironmentName,
            MigrationStatus: GetMigrationStatusLabel(migrationState.Status),
            MigrationReady: migrationState.IsReady,
            GeneratedAt: DateTimeOffset.UtcNow);

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
    DateTimeOffset GeneratedAt);

// The proposal event carries only dashboard fields and never the execution payload.
internal sealed record ProposalRealtimeEvent(
    string ProposalId,
    string Kind,
    string Status,
    string? RequestedBy,
    string RiskLevel,
    DateTimeOffset GeneratedAt);

[JsonSerializable(typeof(AdminRealtimeStatus))]
[JsonSerializable(typeof(ProposalRealtimeEvent))]
internal sealed partial class AdminRealtimeJsonContext : JsonSerializerContext;

