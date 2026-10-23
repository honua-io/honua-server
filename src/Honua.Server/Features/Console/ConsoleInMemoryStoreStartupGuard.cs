// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Console;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console;

/// <summary>
/// Logs when Console content, share, and open-data stores are process-local,
/// and refuses to start when more than one replica is configured for them.
/// </summary>
internal sealed class ConsoleInMemoryStoreStartupGuard : IHostedService
{
    private readonly ILogger<ConsoleInMemoryStoreStartupGuard> _logger;
    private readonly IConfiguration _configuration;
    private readonly ConsolePersistenceNotice _notice;

    public ConsoleInMemoryStoreStartupGuard(
        ILogger<ConsoleInMemoryStoreStartupGuard> logger,
        IConfiguration configuration,
        ConsolePersistenceNotice notice)
    {
        _logger = logger;
        _configuration = configuration;
        _notice = notice;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_notice.Durable)
        {
            return Task.CompletedTask;
        }

        var replicas = ConsoleStoreReplicaPolicy.ResolveExpectedReplicas(
            _configuration[ConsoleStoreReplicaPolicy.ConsoleExpectedReplicaCountKey],
            _configuration[ConsoleStoreReplicaPolicy.DeploymentReplicaCountKey],
            _configuration[ConsoleStoreReplicaPolicy.ServerReplicasKey]);
        ConsoleEndpointsLog.InMemoryConsoleStores(_logger, replicas);
        ConsoleStoreReplicaPolicy.EnsureSingleReplicaWhenNotDurable(durable: false, replicas);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
