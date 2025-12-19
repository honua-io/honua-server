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
    .WaitFor(db);

builder.Build().Run();