// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using Honua.Postgres; // ✅ COMPOSITION ROOT: Allowed to reference Infrastructure
using Honua.Server.Endpoints;
using Serilog;
using Serilog.Enrichers.Span;

// CLEAN ARCHITECTURE COMPOSITION ROOT
// This is the application layer that wires dependencies:
// - Core (abstractions): IDatabaseHealthChecker interface
// - Infrastructure (implementations): PostgresDatabaseHealthChecker
// - Server (composition): Registers IDatabaseHealthChecker → PostgresDatabaseHealthChecker
// Dependency flow: Server → (Core + Infrastructure), Infrastructure → Core

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog for structured logging with AOT compatibility
builder.Host.UseSerilog((context, services, config) =>
{
    var isDevelopment = context.HostingEnvironment.IsDevelopment();

    config
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", Serilog.Events.LogEventLevel.Information)
        .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithSpan()  // OpenTelemetry trace/span IDs
        .Enrich.WithProperty("Application", "Honua")
        .Enrich.WithProperty("Version", typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown");

    if (isDevelopment)
    {
        // Development: Human-readable console output
        config.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");
    }
    else
    {
        // Production: Compact JSON for log aggregation
        config.WriteTo.Console(formatter: new Serilog.Formatting.Compact.CompactJsonFormatter());
    }
});

// DEPENDENCY INVERSION: Register Infrastructure implementations for Core abstractions
// IDatabaseHealthChecker (Core abstraction) → PostgresDatabaseHealthChecker (Infrastructure impl)
builder.Services.AddPostgreSqlServices();

// Register health check services
builder.Services.AddScoped<Honua.Server.Infrastructure.HealthCheck.IReadinessCheckService,
    Honua.Server.Infrastructure.HealthCheck.ReadinessCheckService>();

var app = builder.Build();

// Configure Serilog request logging with custom enrichment
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        diagnosticContext.Set("Protocol", httpContext.Request.Protocol);

        if (httpContext.User.Identity?.IsAuthenticated == true)
            diagnosticContext.Set("UserId", httpContext.User.FindFirst("sub")?.Value);
    };

    // Exclude health check endpoints from request logging (configured in appsettings.json)
    options.GetLevel = (httpContext, elapsed, ex) => ex != null
        ? Serilog.Events.LogEventLevel.Error
        : httpContext.Request.Path.StartsWithSegments("/healthz")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
});

// Log application startup
Honua.Server.Infrastructure.Logging.Log.ApplicationStarting(app.Logger,
    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
    app.Environment.EnvironmentName);

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
        // Skip migrations if no connection string is configured
        Honua.Server.Infrastructure.Logging.Log.DatabaseConnectionStringNotConfigured(app.Logger);
        return;
    }

    Honua.Server.Infrastructure.Logging.Log.DatabaseMigrationsStarting(app.Logger);

    try
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .WithTransaction()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            Honua.Server.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, result.Error.Message, result.Error);
            // Don't throw - let the app start and rely on health checks to indicate readiness
            return;
        }

        if (result.Scripts.Any())
        {
            Honua.Server.Infrastructure.Logging.Log.DatabaseMigrationsCompleted(app.Logger, result.Scripts.Count());
            // Log individual script names for debugging
            foreach (var script in result.Scripts)
            {
                Honua.Server.Infrastructure.Logging.Log.MigrationScriptApplied(app.Logger, script.Name);
            }
        }
        else
        {
            Honua.Server.Infrastructure.Logging.Log.NoDatabaseMigrationsToApply(app.Logger);
        }
    }
    catch (Exception ex)
    {
        Honua.Server.Infrastructure.Logging.Log.DatabaseMigrationFailed(app.Logger, ex.Message, ex);
        // Don't throw - let the app start and rely on health checks to indicate readiness
    }
}


// Make Program accessible to WebApplicationFactory
public partial class Program { }
