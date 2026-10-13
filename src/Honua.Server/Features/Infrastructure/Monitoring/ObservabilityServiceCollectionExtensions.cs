// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Compression;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Operations.Status;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Monitoring;

internal static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        bool redisCacheEntitled = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        ConfigureOutputCaching(services, configuration, redisCacheEntitled);
        services.AddSingleton<OutputCacheInvalidationService>();
        services.AddETags();
        services.TryAddSingleton<ISystemMetricsCollector, SystemMetricsCollector>();
        services.AddPerformanceMonitoring();
        services.TryAddSingleton<ConnectionPoolMetrics>();
        services.AddSingleton<ProductionMetricsCollector>();

        // Source the db-bounded-admission-pressure finding from the REAL admission gate when it is registered
        // (Postgres provider) — it has live in-flight/limit/waiter state and a windowed acquisition-timeout
        // count (#2805). Only when the gate is absent (non-Postgres providers) do we fall back to the
        // ProductionMetricsCollector-backed signal, whose pool counters otherwise have no production writer.
        services.AddSingleton<IOpsDatabasePressureSignal>(sp =>
        {
            var admissionGate = sp.GetService<Honua.Core.Features.Infrastructure.Abstractions.IRuntimeTunableAdmissionGate>();
            return admissionGate is not null
                ? new AdmissionGateDatabasePressureSignal(admissionGate)
                : new ProductionMetricsDatabasePressureSignal(sp.GetRequiredService<ProductionMetricsCollector>());
        });
        services.Configure<RecentErrorBufferOptions>(
            configuration.GetSection(RecentErrorBufferOptions.SectionName));
        services.AddSingleton<RecentErrorBuffer>();

        // Audit-A1 / ADR-0044: RecentErrorBuffer lives in the Server-side
        // Monitoring sub-area (not yet carved into Honua.Hosting). The
        // hosting-layer error formatter records errors through this delegate
        // so the formatter does not take a compile-time reference back to
        // Server. The delegate resolves the buffer per-request via
        // HttpContext.RequestServices to preserve the original lifetime.
        StandardErrorResponseFormatter.RecentErrorBufferRecordOverride = (context, errorResponse) =>
        {
            var buffer = context.RequestServices?.GetService<RecentErrorBuffer>();
            buffer?.Record(context, errorResponse);
        };
        ConfigureResponseCompression(services);

        AddOperateObservability(services);

        // Ops-health snapshot composer + deterministic ops-findings engine (ADR-0060 WS4 / #2457).
        // Both are scoped because they compose the scoped deploy-preflight probe.
        services.AddOptions<OpsFindingsOptions>()
            .Bind(configuration.GetSection(OpsFindingsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<OpsAutonomyOptions>()
            .Bind(configuration.GetSection(OpsAutonomyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<IOpsHealthSnapshotService, OpsHealthSnapshotService>();
        // Singleton: the scoped engine must still report a store source's last successful collection
        // after a failed read in a later scope (#4840).
        services.AddSingleton<OpsFindingsCollectionLedger>();
        services.AddScoped(sp => new OpsFindingsExtendedSignals
        {
            DatabasePressureSignal = sp.GetService<IOpsDatabasePressureSignal>(),
            AdmissionGate = sp.GetService<Honua.Core.Features.Infrastructure.Abstractions.IRuntimeTunableAdmissionGate>(),
            RollupStore = sp.GetService<IOpsHealthRollupStore>(),
            CollectionLedger = sp.GetRequiredService<OpsFindingsCollectionLedger>(),
            TimeProvider = sp.GetService<TimeProvider>(),
        });
        // One scoped instance behind both seams: callers that only need findings keep the domain
        // abstraction, while the read/proposal surfaces resolve the evidence-carrying view.
        services.AddScoped<OpsFindingsService>();
        services.AddScoped<IOpsFindingsService>(sp => sp.GetRequiredService<OpsFindingsService>());
        services.AddScoped<IOpsFindingsEvidenceSource>(sp => sp.GetRequiredService<OpsFindingsService>());
        services.AddScoped<IOpsAutonomyEvaluator, OpsAutonomyEvaluator>();
        services.AddScoped<IMcpOpsObservabilityReader, McpOpsObservabilityReader>();
        services.AddScoped<IMcpPlatformOpsReader, McpPlatformOpsReader>();

        // Persisted ops-health rollup store + per-replica sampler (#2553). The store itself is registered by
        // the Postgres provider; the sampler and history service resolve it as optional so non-Postgres
        // deployments still expose the (empty) history surface and the realtime flush seam. The flush signal
        // is the in-process seam the realtime broadcast layer (#2554) subscribes to.
        services.AddOptions<OpsHealthRollupOptions>()
            .Bind(configuration.GetSection(OpsHealthRollupOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OpsHealthFlushSignal>();
        services.AddScoped<IOpsHealthHistoryService, OpsHealthHistoryService>();
        services.AddHostedService<OpsHealthRollupSampler>();

        // Aggregated operate-status surface (A12): composes the ops-health snapshot + findings into
        // one server-authoritative verdict, and binds the SLO/error-budget contract.
        services.AddOperateStatus(configuration);

        return services;
    }

    /// <summary>
    /// Register the Console Operate observability feed (#1168) and its
    /// background OTLP probe. The probe stays opt-in: when no OTLP endpoint
    /// is configured it reports <c>disabled</c> without generating outbound
    /// traffic, so air-gapped deployments are unaffected.
    /// </summary>
    private static void AddOperateObservability(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(BackgroundOtlpProbe.HttpClientName);
        services.AddSingleton<BackgroundOtlpProbe>();
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundOtlpProbe>());

        services.AddScoped<IOperateEventFeed, LocalOperateEventFeed>();
    }

    internal static void ConfigureOutputCaching(IServiceCollection services, IConfiguration configuration, bool redisCacheEntitled = false)
        => OutputCachingRegistration.ConfigureOutputCaching(services, configuration, redisCacheEntitled);

    internal static bool ShouldUseRedisOutputCache(bool redisCacheEntitled, string? redisConnectionString)
        => OutputCachingRegistration.ShouldUseRedisOutputCache(redisCacheEntitled, redisConnectionString);

    internal static bool HasLiveOutputCacheEntitlements(IServiceProvider services, bool redisOutputCacheConfigured)
        => OutputCachingRegistration.HasLiveOutputCacheEntitlements(services, redisOutputCacheConfigured);

    internal static KeyValuePair<string, string> ResolveLicenseOutputCacheKey(HttpContext context)
        => OutputCachingRegistration.ResolveLicenseOutputCacheKey(context);

    // Configure response compression for GeoJSON and JSON responses
    private static void ConfigureResponseCompression(IServiceCollection services)
    {
        // MIME types for geospatial data formats
        string[] additionalMimeTypes =
        [
            "application/geo+json"     // GeoJSON format
        ];

        services.AddResponseCompression(options =>
        {
            // Enable compression for HTTPS requests
            options.EnableForHttps = true;

            // PERFORMANCE FIX: Use adaptive compression providers for content-size based optimization
            options.Providers.Add<AdaptiveBrotliCompressionProvider>();
            options.Providers.Add<AdaptiveGzipCompressionProvider>();

            // Add MIME types for geospatial data formats
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(additionalMimeTypes);
        });

        // PERFORMANCE FIX: Configure adaptive compression that adjusts level based on content size
        services.Configure<AdaptiveCompressionOptions>(options =>
        {
            // Small content (< 4KB): Use fastest compression for low latency
            options.SmallContentLevel = CompressionLevel.Fastest;
            // Medium and large API payloads are frequently CPU-bound in practice; favor
            // low-latency compression over maximal byte reduction on the hot path.
            options.MediumContentLevel = CompressionLevel.Fastest;
            options.LargeContentLevel = CompressionLevel.Fastest;
        });

        // Register adaptive compression providers
        services.TryAddTransient<AdaptiveBrotliCompressionProvider>();
        services.TryAddTransient<AdaptiveGzipCompressionProvider>();
    }
}
