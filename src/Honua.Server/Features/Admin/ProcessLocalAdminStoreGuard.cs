// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Admin;

// Some admin settings have only a store held in the memory of the task that answers the request. With more than one
// task a change there reaches one task, the others keep the old setting, and the change is gone after a restart.
// Until those settings have a shared store, a deployment configured for several tasks refuses to change them.
internal static class ProcessLocalAdminStoreGuard
{
    internal static RouteHandlerBuilder RefuseWhenProcessLocalOnSeveralTasks<TStore, TProcessLocalStore>(
        this RouteHandlerBuilder builder,
        string settingsName)
        where TStore : class
        where TProcessLocalStore : TStore
        => builder.AddEndpointFilter(async (invocation, next) =>
        {
            var services = invocation.HttpContext.RequestServices;
            if (services.GetService<TStore>() is TProcessLocalStore)
            {
                var tasks = DeploymentTaskCount.Resolve(services.GetRequiredService<IConfiguration>());
                if (tasks > 1)
                {
                    return StandardErrorHelpers.CreateConflict(invocation.HttpContext, RefusalDetail(settingsName, tasks));
                }
            }

            return await next(invocation).ConfigureAwait(false);
        });

    internal static string RefusalDetail(string settingsName, int tasks)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{settingsName} are held in the memory of the server task that receives the change, and this deployment is configured to run more than one task ({tasks} expected; {DeploymentTaskCount.MaxTasksKey}, {DeploymentTaskCount.ReplicaCountKey}, {DeploymentTaskCount.ServerReplicasKey} or {DeploymentTaskCount.ModeKey}). The change would reach one task only and be lost when it restarts, so it was not made. Set them in the server configuration, or make the change on a deployment configured for a single task.");
}
