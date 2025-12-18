// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using Honua.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// TODO: Add services

var app = builder.Build();

// Run database migrations on startup
await RunDatabaseMigrationsAsync();

// Configure health endpoints
app.MapHealthEndpoints();

app.Run();

// Database migration helper
async Task RunDatabaseMigrationsAsync()
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrEmpty(connectionString))
    {
        DatabaseLogger.ConnectionStringNotConfigured(app.Logger);
        return; // Skip migrations if no connection string is configured
    }

    DatabaseLogger.RunningMigrations(app.Logger);

    var upgrader = DeployChanges.To
        .PostgresqlDatabase(connectionString)
        .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
        .WithTransaction()
        .LogToConsole()
        .Build();

    var result = upgrader.PerformUpgrade();

    if (!result.Successful)
    {
        DatabaseLogger.MigrationFailed(app.Logger, result.Error);
        // Don't throw - let the app start and rely on health checks to indicate readiness
        return;
    }

    DatabaseLogger.MigrationsCompleted(app.Logger);

    if (result.Scripts.Any())
    {
        DatabaseLogger.MigrationScriptsApplied(app.Logger, result.Scripts.Count());
        foreach (var script in result.Scripts)
        {
            DatabaseLogger.MigrationScriptApplied(app.Logger, script.Name);
        }
    }
    else
    {
        DatabaseLogger.NoMigrationsToApply(app.Logger);
    }
}

// Source-generated logging for AOT compatibility
internal static partial class DatabaseLogger
{
    [LoggerMessage(
        EventId = 1001,
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Database connection string 'DefaultConnection' not configured - skipping migrations")]
    public static partial void ConnectionStringNotConfigured(Microsoft.Extensions.Logging.ILogger logger);

    [LoggerMessage(
        EventId = 1002,
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Running database migrations...")]
    public static partial void RunningMigrations(Microsoft.Extensions.Logging.ILogger logger);

    [LoggerMessage(
        EventId = 1003,
        Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "Database migration failed")]
    public static partial void MigrationFailed(Microsoft.Extensions.Logging.ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1004,
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Database migrations completed successfully")]
    public static partial void MigrationsCompleted(Microsoft.Extensions.Logging.ILogger logger);

    [LoggerMessage(
        EventId = 1005,
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Applied {ScriptCount} migration scripts")]
    public static partial void MigrationScriptsApplied(Microsoft.Extensions.Logging.ILogger logger, int scriptCount);

    [LoggerMessage(
        EventId = 1006,
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "  - {ScriptName}")]
    public static partial void MigrationScriptApplied(Microsoft.Extensions.Logging.ILogger logger, string scriptName);

    [LoggerMessage(
        EventId = 1007,
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "No new migrations to apply")]
    public static partial void NoMigrationsToApply(Microsoft.Extensions.Logging.ILogger logger);
}

// Make Program accessible to WebApplicationFactory
public partial class Program { }
