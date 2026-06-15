// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Default <see cref="IAdminConfigChangeApplier"/>. Admin configuration changes
/// in v1 are recorded and audited through the gateway; the concrete change is
/// applied by the originating admin pipeline. This applier records the apply step
/// and returns the change identifier so the proposal can link to it. Deployments
/// that wire a richer admin-config apply pipeline can replace this registration.
/// </summary>
internal sealed partial class LoggingAdminConfigChangeApplier(
    ILogger<LoggingAdminConfigChangeApplier> logger) : IAdminConfigChangeApplier
{
    public Task<string?> ApplyAsync(string? changePayload, CancellationToken cancellationToken = default)
    {
        var changeId = $"adminconfig-{Guid.NewGuid():N}";
        Log.AdminConfigChangeApplied(logger, changeId);
        return Task.FromResult<string?>(changeId);
    }

    private static partial class Log
    {
        [LoggerMessage(9400, LogLevel.Information, "Applied admin configuration change {ChangeId}")]
        public static partial void AdminConfigChangeApplied(ILogger logger, string changeId);
    }
}
