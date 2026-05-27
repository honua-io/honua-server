// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Worker.Gdal;

// Heavyweight GDAL/ETL worker entrypoint (ADR-0038 honua-worker-etl image).
//
// This is a headless Generic Host — NOT a web server. It runs the SAME durable
// job-execution loop the lean serving image hosts, but registers only the
// native-profile GDAL executors, so it claims ONLY jobs whose RuntimeProfile is
// "native" (raster reproject, vector conversion, and future heavy GP families).
// It is never reachable from public ingress.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddGdalWorker(builder.Configuration);

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
