// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL with PostGIS
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgis/postgis", "16-3.4")
    .WithDataVolume("honua-postgres-data")
    .WithPgAdmin();

var db = postgres.AddDatabase("honua");

// Optional Redis for caching
var redis = builder.AddRedis("redis")
    .WithRedisCommander();

// Honua Server
var honua = builder.AddProject("honua-server", "../Honua.Server/Honua.Server.csproj")
    .WithReference(db)
    .WithReference(redis)
    .WaitFor(db)
    .WaitFor(redis);

builder.Build().Run();
