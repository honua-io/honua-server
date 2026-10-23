// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal;

/// <summary>
/// Logs the GDAL worker's Redis durability attestation outcome once at startup. The outcome is
/// information (owner ruling 2026-10-10), never a reason to refuse work: the worker runs on any
/// connected Redis unless <c>Jobs:RequireDurableStore=true</c> was set.
/// </summary>
internal sealed partial class GdalWorkerRedisDurabilityReporter(
    bool attested,
    DurableJobSubstrateCause? cause,
    string? detail,
    ILogger<GdalWorkerRedisDurabilityReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var status = RedisDurabilityStatuses.For(attested, cause) ?? RedisDurabilityStatuses.Unverified;
        if (attested)
        {
            LogAttested(logger, status);
        }
        else
        {
            LogNotAttested(logger, status, cause, detail ?? "no detail reported");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis durability attestation: {Status}.")]
    private static partial void LogAttested(ILogger logger, string status);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Redis durability attestation: {Status} ({Cause}: {Detail}). Informational: the worker runs on any connected Redis; durability is an operator property of the Redis deployment. Set Jobs:RequireDurableStore=true to refuse startup instead.")]
    private static partial void LogNotAttested(ILogger logger, string status, DurableJobSubstrateCause? cause, string detail);
}
