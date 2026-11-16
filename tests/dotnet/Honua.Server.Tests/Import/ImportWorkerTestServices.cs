// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Migration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Import;

internal static class ImportWorkerTestServices
{
    public static void RemoveHostedImportWorkers(IServiceCollection services)
    {
        Type[] workers = [typeof(JobExecutionService), typeof(MigrationBatchBackgroundService)];
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                     workers.Contains(descriptor.ImplementationType)).ToArray())
        {
            services.Remove(descriptor);
        }
    }
}
