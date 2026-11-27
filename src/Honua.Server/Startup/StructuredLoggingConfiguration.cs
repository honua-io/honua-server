// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Logging;
using Honua.Infrastructure.Middleware;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Context;
using Serilog.Enrichers.Span;
using Serilog.Events;

namespace Honua.Server.Startup;

internal static class StructuredLoggingConfiguration
{
    private const LogEventLevel LevelOff = (LogEventLevel)(1 + (int)LogEventLevel.Fatal);

    public static void ConfigureStructuredLogging(this WebApplicationBuilder builder)
    {
        // Call before telemetry registration so only the hosting defaults are removed.
        // Serilog owns console output; later providers still receive structured events.
        builder.Logging.ClearProviders();
        builder.Services.TryAddSingleton<ITenantLogContextScopeProvider, SerilogTenantLogContextScopeProvider>();
        builder.Host.UseSerilog((context, services, config) => Configure(context, config),
            preserveStaticLogger: false, writeToProviders: true);
    }

    private static void Configure(HostBuilderContext context, LoggerConfiguration config)
    {
        var isDevelopment = context.HostingEnvironment.IsDevelopment();
        var benchmarkQuietLogs = context.Configuration.GetValue<bool>("HONUA_BENCHMARK_QUIET_LOGS");

        ConfigureLogLevels(context.Configuration, config);

        config
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.WithSpan()  // OpenTelemetry trace/span IDs
            .Enrich.WithProperty("Application", "Honua")
            .Enrich.WithProperty("Version", typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown")
            .ConfigureHonuaRequestDiagnostics();

        if (benchmarkQuietLogs)
        {
            ConfigureQuietLogging(config);
        }

        if (isDevelopment)
        {
            // Development: Human-readable console output
            config.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            // Production: Compact JSON for log aggregation
            config.WriteTo.Console(formatter: new Serilog.Formatting.Compact.CompactJsonFormatter());
        }
    }

    internal static void ConfigureLogLevels(IConfiguration configuration, LoggerConfiguration config)
    {
        var logLevelSection = configuration.GetSection("Logging:LogLevel");
        var defaultLevel = ParseLogLevel(logLevelSection["Default"]) ?? LogEventLevel.Information;

        config
            .MinimumLevel.Is(defaultLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning);

        foreach (var entry in logLevelSection.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(entry.Key)
                || string.Equals(entry.Key, "Default", StringComparison.OrdinalIgnoreCase)
                || ParseLogLevel(entry.Value) is not { } level)
            {
                continue;
            }

            config.MinimumLevel.Override(entry.Key, level);
        }
    }

    internal static LogEventLevel? ParseLogLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "trace" or "verbose" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" => LogEventLevel.Information,
            "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "critical" or "fatal" => LogEventLevel.Fatal,
            "none" => LevelOff,
            _ => null
        };
    }

    private static void ConfigureQuietLogging(LoggerConfiguration config)
    {
        config
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
            .MinimumLevel.Override("Serilog.AspNetCore.RequestLoggingMiddleware", LogEventLevel.Warning)
            .MinimumLevel.Override("Honua.Infrastructure.Middleware.SecurityHeadersMiddleware", LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Infrastructure.Authentication.ApiKeyAuthenticationHandler", LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Protocols.Ogc.Api.Features.OgcFeaturesQueryHandler", LogEventLevel.Warning);
    }

    private sealed class SerilogTenantLogContextScopeProvider : ITenantLogContextScopeProvider
    {
        public IDisposable PushTenantId(string tenantId)
            => LogContext.PushProperty("TenantId", tenantId);
    }
}
