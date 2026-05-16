// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Monitoring;
using Microsoft.AspNetCore.SignalR;

namespace Honua.Server.Features.Admin;

internal static class AdminRealtimeHubExtensions
{
    public static IServiceCollection AddAdminRealtime(this IServiceCollection services)
    {
        services
            .AddSignalR()
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
            .RequireAdminAuthorization();
}

internal static class AdminRealtimeContract
{
    internal const string HubPath = "/hubs/admin";
    internal const string Protocol = "signalr";
    internal const string StatusChangedEventName = "AdminStatusChanged";
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

[JsonSerializable(typeof(AdminRealtimeStatus))]
internal sealed partial class AdminRealtimeJsonContext : JsonSerializerContext;
