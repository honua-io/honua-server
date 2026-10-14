// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Infrastructure.Licensing;
using Honua.Worker.Gdal;

// Heavyweight GDAL/ETL worker entrypoint (ADR-0038 honua-worker-etl image).
//
// This is a headless Generic Host — NOT a web server. It runs the SAME durable
// job-execution loop the lean serving image hosts, but registers only the
// native-profile GDAL executors, so it claims ONLY jobs whose RuntimeProfile is
// "native" (raster reproject, vector conversion, and future heavy GP families).
// It is never reachable from public ingress.
var builder = Host.CreateApplicationBuilder(args);
var composition = HostCompositionSelection.Resolve(builder.Configuration);
var licenseBootstrapServices = new ServiceCollection();
licenseBootstrapServices.AddLogging(logging => logging.AddConsole());
GdalWorkerServiceCollectionExtensions.AddLicenseSecretResolvers(licenseBootstrapServices, builder.Configuration);
using (var licenseBootstrap = licenseBootstrapServices.BuildServiceProvider())
{
    await FileBackedLicenseService.LoadBootstrapSnapshotAsync(builder.Configuration,
        licenseBootstrap.GetRequiredService<ILoggerFactory>(),
        secretResolvers: licenseBootstrap.GetServices<ILicenseContentSecretResolver>());
}

builder.Services.AddGdalWorker(builder.Configuration);

if (composition.IsExecutionWorker)
{
    builder.Services.AddAssignedExecutionWorker(composition.Launch!);
}

var host = builder.Build();
if (composition.IsExecutionWorker)
{
    Environment.ExitCode = await host.RunExecutionWorkerAsync().ConfigureAwait(false);
}
else
{
    await host.RunAsync().ConfigureAwait(false);
}
