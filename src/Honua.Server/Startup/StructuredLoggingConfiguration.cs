// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Logging;
using Serilog;
using Serilog.Enrichers.Span;

namespace Honua.Server.Startup;

internal static class StructuredLoggingConfiguration
{
    public static void ConfigureStructuredLogging(this WebApplicationBuilder builder)
    {
        // Call before telemetry registration so only the hosting defaults are removed.
        // Serilog owns console output; later providers still receive structured events.
        builder.Logging.ClearProviders();
        builder.Host.UseSerilog((context, services, config) => Configure(context, config),
            preserveStaticLogger: false, writeToProviders: true);
    }

    private static void Configure(HostBuilderContext context, LoggerConfiguration config)
    {
        var isDevelopment = context.HostingEnvironment.IsDevelopment();
        var benchmarkQuietLogs = context.Configuration.GetValue<bool>("HONUA_BENCHMARK_QUIET_LOGS");

        config
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
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

    private static void ConfigureQuietLogging(LoggerConfiguration config)
    {
        config
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Serilog.AspNetCore.RequestLoggingMiddleware", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Honua.Infrastructure.Middleware.SecurityHeadersMiddleware", Serilog.Events.LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Infrastructure.Authentication.ApiKeyAuthenticationHandler", Serilog.Events.LogEventLevel.Error)
            .MinimumLevel.Override("Honua.Protocols.Ogc.Api.Features.OgcFeaturesQueryHandler", Serilog.Events.LogEventLevel.Warning);
    }
}
