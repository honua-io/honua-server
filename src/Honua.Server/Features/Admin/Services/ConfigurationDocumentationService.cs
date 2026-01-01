// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Middleware;
using Microsoft.Extensions.Options;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Service for building self-documenting configuration metadata.
/// </summary>
internal sealed class ConfigurationDocumentationService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IOptions<LimitsOptions> _limitsOptions;
    private readonly IOptions<CacheOptions> _cacheOptions;
    private readonly IOptions<TileOptions> _tileOptions;
    private readonly IOptions<RateLimitOptions> _rateLimitOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationDocumentationService"/> class.
    /// </summary>
    public ConfigurationDocumentationService(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IOptions<LimitsOptions> limitsOptions,
        IOptions<CacheOptions> cacheOptions,
        IOptions<TileOptions> tileOptions,
        IOptions<RateLimitOptions> rateLimitOptions)
    {
        _configuration = configuration;
        _environment = environment;
        _limitsOptions = limitsOptions;
        _cacheOptions = cacheOptions;
        _tileOptions = tileOptions;
        _rateLimitOptions = rateLimitOptions;
    }

    /// <summary>
    /// Builds the complete configuration documentation.
    /// </summary>
    public ConfigurationDocumentation BuildDocumentation()
    {
        var sections = new List<ConfigurationSection>
        {
            BuildFeatureFlagsSection(),
            BuildDatabaseSection(),
            BuildCacheSection(),
            BuildLimitsQuerySection(),
            BuildLimitsGeometrySection(),
            BuildLimitsEditsSection(),
            BuildLimitsAttachmentsSection(),
            BuildLimitsTilesSection(),
            BuildLimitsConnectionsSection(),
            BuildLimitsImportsSection(),
            BuildRateLimitSection(),
            BuildTileOptionsSection(),
            BuildSecuritySection()
        };

        var envVars = BuildEnvironmentVariableQuickReference();
        var version = typeof(ConfigurationDocumentationService).Assembly.GetName().Version?.ToString() ?? "unknown";

        return new ConfigurationDocumentation
        {
            Sections = sections,
            EnvironmentVariables = envVars,
            Version = version,
            Environment = _environment.EnvironmentName
        };
    }

    private ConfigurationSection BuildFeatureFlagsSection()
    {
        return new ConfigurationSection
        {
            Name = "Features",
            Description = "Feature flags for enabling/disabling server capabilities",
            Properties =
            [
                BuildProperty("HONUA_ADMIN_UI", "HONUA_ADMIN_UI", "boolean",
                    "Enables the web admin interface", false, isSensitive: false),
                BuildProperty("HONUA_OBSERVABILITY", "HONUA_OBSERVABILITY", "boolean",
                    "Activates metrics and health endpoints", false, isSensitive: false),
                BuildProperty("HONUA_OPENTELEMETRY", "HONUA_OPENTELEMETRY", "boolean",
                    "Controls distributed tracing with OpenTelemetry", false, isSensitive: false),
                BuildProperty("HONUA_SKIP_MIGRATIONS", "HONUA_SKIP_MIGRATIONS", "boolean",
                    "Skip database migrations on startup", false, isSensitive: false),
                BuildProperty("HONUA_TEST_SCHEMA_HEADERS", "HONUA_TEST_SCHEMA_HEADERS", "boolean",
                    "Enable schema-based test isolation headers", false, isSensitive: false),
                BuildProperty("HONUA_DEV_AUTH", "HONUA_DEV_AUTH", "string",
                    "Development authentication bypass token", null, isSensitive: true)
            ]
        };
    }

    private ConfigurationSection BuildDatabaseSection()
    {
        return new ConfigurationSection
        {
            Name = "Database",
            Description = "Database connection and query settings",
            Properties =
            [
                BuildProperty("ConnectionStrings:DefaultConnection", "ConnectionStrings__DefaultConnection", "string",
                    "PostgreSQL connection string", null, isRequired: true, isSensitive: true),
                BuildProperty("Database:QueryCache:MaxCachedStatements", "Database__QueryCache__MaxCachedStatements", "integer",
                    "Maximum number of cached prepared statements", 100),
                BuildProperty("Database:QueryCache:StatementLifetimeMinutes", "Database__QueryCache__StatementLifetimeMinutes", "integer",
                    "Lifetime of cached statements in minutes", 30),
                BuildProperty("Database:QueryCache:MinExecutionsForCaching", "Database__QueryCache__MinExecutionsForCaching", "integer",
                    "Minimum executions before a statement is cached", 3),
                BuildProperty("Database:QueryCache:EnableAutomaticCaching", "Database__QueryCache__EnableAutomaticCaching", "boolean",
                    "Enable automatic statement caching", true),
                BuildProperty("Database:QueryCache:EnablePerformanceLogging", "Database__QueryCache__EnablePerformanceLogging", "boolean",
                    "Enable performance logging for cached queries", false)
            ]
        };
    }

    private ConfigurationSection BuildCacheSection()
    {
        var opts = _cacheOptions.Value;
        return new ConfigurationSection
        {
            Name = "Cache",
            Description = "Redis metadata caching configuration",
            Properties =
            [
                BuildPropertyWithCurrent("Cache:Enabled", "Cache__Enabled", "boolean",
                    "Whether caching is enabled", true, opts.Enabled),
                BuildPropertyWithCurrent("Cache:DefaultTtlSeconds", "Cache__DefaultTtlSeconds", "integer",
                    "Default cache TTL in seconds", 300, opts.DefaultTtlSeconds, "Range: 1-86400"),
                BuildPropertyWithCurrent("Cache:ServiceTtlSeconds", "Cache__ServiceTtlSeconds", "integer",
                    "Service metadata cache TTL in seconds", 300, opts.ServiceTtlSeconds, "Range: 1-86400"),
                BuildPropertyWithCurrent("Cache:LayerTtlSeconds", "Cache__LayerTtlSeconds", "integer",
                    "Layer metadata cache TTL in seconds", 300, opts.LayerTtlSeconds, "Range: 1-86400"),
                BuildPropertyWithCurrent("Cache:EnableFallback", "Cache__EnableFallback", "boolean",
                    "Use in-memory fallback when Redis unavailable", true, opts.EnableFallback),
                BuildPropertyWithCurrent("Cache:FallbackMaxEntries", "Cache__FallbackMaxEntries", "integer",
                    "Maximum entries in fallback cache", 1000, opts.FallbackMaxEntries, "Range: 10-100000"),
                BuildPropertyWithCurrent("Cache:RetryIntervalSeconds", "Cache__RetryIntervalSeconds", "integer",
                    "Retry interval after Redis failure", 30, opts.RetryIntervalSeconds, "Range: 5-300"),
                BuildPropertyWithCurrent("Cache:KeyPrefix", "Cache__KeyPrefix", "string",
                    "Prefix for cache keys", "honua:", opts.KeyPrefix)
            ]
        };
    }

    private ConfigurationSection BuildLimitsQuerySection()
    {
        var opts = _limitsOptions.Value.Query;
        return new ConfigurationSection
        {
            Name = "Limits.Query",
            Description = "Query operation limits applied to all protocols",
            Properties =
            [
                BuildPropertyWithCurrent("Limits:Query:MaxRecordCount", "Limits__Query__MaxRecordCount", "integer",
                    "Maximum features per query response", 2000, opts.MaxRecordCount, "Range: 100-10000"),
                BuildPropertyWithCurrent("Limits:Query:DefaultRecordCount", "Limits__Query__DefaultRecordCount", "integer",
                    "Default features when not specified", 1000, opts.DefaultRecordCount, "Range: 100+"),
                BuildPropertyWithCurrent("Limits:Query:MaxOffset", "Limits__Query__MaxOffset", "integer",
                    "Maximum pagination offset", 100000, opts.MaxOffset, "Range: 1000-1000000"),
                BuildPropertyWithCurrent("Limits:Query:MaxBboxAreaSqKm", "Limits__Query__MaxBboxAreaSqKm", "number",
                    "Maximum bounding box area in square km", 1000.0, opts.MaxBboxAreaSqKm),
                BuildPropertyWithCurrent("Limits:Query:QueryTimeout", "Limits__Query__QueryTimeout", "timespan",
                    "Query timeout (HH:MM:SS format)", TimeSpan.FromSeconds(30), opts.QueryTimeout, "Range: 00:00:05-00:02:00")
            ]
        };
    }

    private ConfigurationSection BuildLimitsGeometrySection()
    {
        var opts = _limitsOptions.Value.Geometry;
        return new ConfigurationSection
        {
            Name = "Limits.Geometry",
            Description = "Geometry processing and validation limits",
            Properties =
            [
                BuildPropertyWithCurrent("Limits:Geometry:MaxVerticesPerGeometry", "Limits__Geometry__MaxVerticesPerGeometry", "integer",
                    "Maximum vertices per geometry", 100000, opts.MaxVerticesPerGeometry, "Range: 1000-1000000"),
                BuildPropertyWithCurrent("Limits:Geometry:MaxGeometrySize", "Limits__Geometry__MaxGeometrySize", "integer",
                    "Maximum geometry size in bytes", 10485760, opts.MaxGeometrySize, "Range: 1MB-100MB"),
                BuildPropertyWithCurrent("Limits:Geometry:MaxCoordinatePrecision", "Limits__Geometry__MaxCoordinatePrecision", "integer",
                    "Maximum coordinate decimal places", 8, opts.MaxCoordinatePrecision, "Range: 1-15"),
                BuildPropertyWithCurrent("Limits:Geometry:SimplifyTolerance", "Limits__Geometry__SimplifyTolerance", "number",
                    "Auto-simplification tolerance in meters (null = disabled)", null, opts.SimplifyTolerance, "Range: 0-1000")
            ]
        };
    }

    private ConfigurationSection BuildLimitsEditsSection()
    {
        var opts = _limitsOptions.Value.Edits;
        return new ConfigurationSection
        {
            Name = "Limits.Edits",
            Description = "Edit operation limits for CRUD operations",
            Properties =
            [
                BuildPropertyWithCurrent("Limits:Edits:MaxFeaturesPerEdit", "Limits__Edits__MaxFeaturesPerEdit", "integer",
                    "Maximum features per edit operation", 1000, opts.MaxFeaturesPerEdit, "Range: 1-10000"),
                BuildPropertyWithCurrent("Limits:Edits:MaxEditsPerTransaction", "Limits__Edits__MaxEditsPerTransaction", "integer",
                    "Maximum operations per transaction", 5000, opts.MaxEditsPerTransaction, "Range: 100-50000"),
                BuildPropertyWithCurrent("Limits:Edits:MaxPayloadSize", "Limits__Edits__MaxPayloadSize", "integer",
                    "Maximum request body size in bytes", 52428800, opts.MaxPayloadSize, "Range: 1MB-500MB")
            ]
        };
    }

    private ConfigurationSection BuildLimitsAttachmentsSection()
    {
        var opts = _limitsOptions.Value.Attachments;
        return new ConfigurationSection
        {
            Name = "Limits.Attachments",
            Description = "File attachment limits",
            Properties =
            [
                BuildPropertyWithCurrent("Limits:Attachments:MaxAttachmentSize", "Limits__Attachments__MaxAttachmentSize", "integer",
                    "Maximum single attachment size in bytes", 10485760, opts.MaxAttachmentSize, "Range: 1MB-100MB"),
                BuildPropertyWithCurrent("Limits:Attachments:MaxAttachmentsPerFeature", "Limits__Attachments__MaxAttachmentsPerFeature", "integer",
                    "Maximum attachments per feature", 10, opts.MaxAttachmentsPerFeature, "Range: 1-100"),
                BuildPropertyWithCurrent("Limits:Attachments:MaxTotalAttachmentSize", "Limits__Attachments__MaxTotalAttachmentSize", "integer",
                    "Maximum total attachment size per feature in bytes", 104857600, opts.MaxTotalAttachmentSize, "Range: 10MB-1GB"),
                BuildPropertyWithCurrent("Limits:Attachments:AllowedMimeTypes", "Limits__Attachments__AllowedMimeTypes", "string",
                    "Comma-separated allowed MIME types", "image/*,application/pdf", opts.AllowedMimeTypes)
            ]
        };
    }

    private ConfigurationSection BuildLimitsTilesSection()
    {
        var opts = _limitsOptions.Value.Tiles;
        return new ConfigurationSection
        {
            Name = "Limits.Tiles",
            Description = "Map tile generation limits",
            Properties =
            [
                BuildPropertyWithCurrent("Limits:Tiles:MaxTileZoom", "Limits__Tiles__MaxTileZoom", "integer",
                    "Maximum tile zoom level", 22, opts.MaxTileZoom, "Range: 1-24"),
                BuildPropertyWithCurrent("Limits:Tiles:MinTileZoom", "Limits__Tiles__MinTileZoom", "integer",
                    "Minimum tile zoom level", 0, opts.MinTileZoom, "Range: 0-10"),
                BuildPropertyWithCurrent("Limits:Tiles:MaxFeaturesPerTile", "Limits__Tiles__MaxFeaturesPerTile", "integer",
                    "Maximum features per tile", 100000, opts.MaxFeaturesPerTile, "Range: 1000-1000000"),
                BuildPropertyWithCurrent("Limits:Tiles:TileTimeout", "Limits__Tiles__TileTimeout", "timespan",
                    "Tile generation timeout", TimeSpan.FromSeconds(10), opts.TileTimeout, "Range: 00:00:01-00:01:00"),
                BuildPropertyWithCurrent("Limits:Tiles:MaxTileSize", "Limits__Tiles__MaxTileSize", "integer",
                    "Maximum compressed tile size in bytes", 512000, opts.MaxTileSize, "Range: 100KB-5MB")
            ]
        };
    }

    private ConfigurationSection BuildLimitsConnectionsSection()
    {
        var opts = _limitsOptions.Value.Connections;
        return new ConfigurationSection
        {
            Name = "Limits.Connections",
            Description = "Database connection and concurrency limits",
            Properties =
            [
                BuildPropertyWithCurrent("Limits:Connections:MaxConcurrentQueries", "Limits__Connections__MaxConcurrentQueries", "integer",
                    "Maximum concurrent query operations", 100, opts.MaxConcurrentQueries, "Range: 10-1000"),
                BuildPropertyWithCurrent("Limits:Connections:MaxConnectionPoolSize", "Limits__Connections__MaxConnectionPoolSize", "integer",
                    "Maximum database connection pool size", 100, opts.MaxConnectionPoolSize, "Range: 10-500"),
                BuildPropertyWithCurrent("Limits:Connections:RequestTimeout", "Limits__Connections__RequestTimeout", "timespan",
                    "Overall request timeout", TimeSpan.FromSeconds(120), opts.RequestTimeout, "Range: 00:00:10-00:10:00")
            ]
        };
    }

    private ConfigurationSection BuildLimitsImportsSection()
    {
        var opts = _limitsOptions.Value.Imports;
        return new ConfigurationSection
        {
            Name = "Limits.Imports",
            Description = "File import operation limits",
            Properties =
            [
                BuildPropertyWithCurrent("Limits:Imports:MaxPreviewSize", "Limits__Imports__MaxPreviewSize", "integer",
                    "Maximum file size for preview in bytes", 10485760, opts.MaxPreviewSize, "Range: 1MB-50MB"),
                BuildPropertyWithCurrent("Limits:Imports:MaxSyncImportSize", "Limits__Imports__MaxSyncImportSize", "integer",
                    "Maximum file size for sync import in bytes", 52428800, opts.MaxSyncImportSize, "Range: 10MB-500MB"),
                BuildPropertyWithCurrent("Limits:Imports:MaxImportSize", "Limits__Imports__MaxImportSize", "integer",
                    "Maximum import file size in bytes", 524288000, opts.MaxImportSize, "Range: 50MB-5GB"),
                BuildPropertyWithCurrent("Limits:Imports:MaxPreviewFeatures", "Limits__Imports__MaxPreviewFeatures", "integer",
                    "Maximum features in preview", 100, opts.MaxPreviewFeatures, "Range: 10-1000"),
                BuildPropertyWithCurrent("Limits:Imports:BatchSize", "Limits__Imports__BatchSize", "integer",
                    "Feature insertion batch size", 1000, opts.BatchSize, "Range: 100-10000")
            ]
        };
    }

    private ConfigurationSection BuildRateLimitSection()
    {
        var opts = _rateLimitOptions.Value;
        return new ConfigurationSection
        {
            Name = "RateLimit",
            Description = "Rate limiting configuration",
            Properties =
            [
                BuildPropertyWithCurrent("RateLimit:MaxRequestsPerWindow", "RateLimit__MaxRequestsPerWindow", "integer",
                    "Maximum requests per time window", opts.MaxRequestsPerWindow, opts.MaxRequestsPerWindow),
                BuildPropertyWithCurrent("RateLimit:WindowSize", "RateLimit__WindowSize", "timespan",
                    "Rate limit window duration", opts.WindowSize, opts.WindowSize),
                BuildPropertyWithCurrent("RateLimit:TrustProxyHeaders", "RateLimit__TrustProxyHeaders", "boolean",
                    "Trust X-Forwarded-For headers", opts.TrustProxyHeaders, opts.TrustProxyHeaders)
            ]
        };
    }

    private ConfigurationSection BuildTileOptionsSection()
    {
        var opts = _tileOptions.Value;
        return new ConfigurationSection
        {
            Name = "TileOptions",
            Description = "Tile generation and caching options",
            Properties =
            [
                BuildPropertyWithCurrent("TileOptions:MaxFeaturesPerTile", "TileOptions__MaxFeaturesPerTile", "integer",
                    "Maximum features per tile", 10000, opts.MaxFeaturesPerTile),
                BuildPropertyWithCurrent("TileOptions:TileTimeoutSeconds", "TileOptions__TileTimeoutSeconds", "integer",
                    "Tile generation timeout in seconds", 10, opts.TileTimeoutSeconds),
                BuildPropertyWithCurrent("TileOptions:SimplifyZoom", "TileOptions__SimplifyZoom", "integer",
                    "Zoom level below which geometries are simplified", 10, opts.SimplifyZoom),
                BuildPropertyWithCurrent("TileOptions:MinZoom", "TileOptions__MinZoom", "integer",
                    "Minimum supported zoom level", 0, opts.MinZoom),
                BuildPropertyWithCurrent("TileOptions:MaxZoom", "TileOptions__MaxZoom", "integer",
                    "Maximum supported zoom level", 22, opts.MaxZoom),
                BuildPropertyWithCurrent("TileOptions:CacheMaxAge", "TileOptions__CacheMaxAge", "integer",
                    "Cache control max-age in seconds", 3600, opts.CacheMaxAge),
                BuildPropertyWithCurrent("TileOptions:TileExtent", "TileOptions__TileExtent", "integer",
                    "MVT tile extent", 4096, opts.TileExtent),
                BuildPropertyWithCurrent("TileOptions:TileBuffer", "TileOptions__TileBuffer", "integer",
                    "MVT buffer size in pixels", 256, opts.TileBuffer)
            ]
        };
    }

    private ConfigurationSection BuildSecuritySection()
    {
        return new ConfigurationSection
        {
            Name = "Security",
            Description = "Authentication and security configuration",
            Properties =
            [
                BuildProperty("HONUA_ADMIN_PASSWORD", "HONUA_ADMIN_PASSWORD", "string",
                    "Admin API password", null, isSensitive: true),
                BuildProperty("Cors:AllowedOrigins:0", "Cors__AllowedOrigins__0", "string",
                    "First allowed CORS origin", null),
                BuildProperty("Cors:AllowCredentials", "Cors__AllowCredentials", "boolean",
                    "Allow credentials in CORS requests", false),
                BuildProperty("SecurityHeaders:EnableHsts", "SecurityHeaders__EnableHsts", "boolean",
                    "Enable HTTP Strict Transport Security", true),
                BuildProperty("SecurityHeaders:HstsMaxAge", "SecurityHeaders__HstsMaxAge", "integer",
                    "HSTS max age in seconds", 31536000)
            ]
        };
    }

    private ConfigurationProperty BuildProperty(string path, string envVar, string type, string description,
        object? defaultValue, bool isRequired = false, bool isSensitive = false, string? validation = null)
    {
        var currentValue = GetCurrentValue(path, isSensitive);
        var source = DetermineSource(path);

        return new ConfigurationProperty
        {
            Name = path.Split(':').Last(),
            Path = path,
            EnvironmentVariable = envVar,
            Type = type,
            Description = description,
            DefaultValue = defaultValue,
            CurrentValue = currentValue,
            IsRequired = isRequired,
            IsSensitive = isSensitive,
            Validation = validation,
            Source = source
        };
    }

    private ConfigurationProperty BuildPropertyWithCurrent(string path, string envVar, string type, string description,
        object? defaultValue, object? currentValue, string? validation = null, bool isSensitive = false)
    {
        var source = DetermineSource(path);
        var displayValue = isSensitive && currentValue != null ? "***" : currentValue;

        return new ConfigurationProperty
        {
            Name = path.Split(':').Last(),
            Path = path,
            EnvironmentVariable = envVar,
            Type = type,
            Description = description,
            DefaultValue = defaultValue,
            CurrentValue = displayValue,
            IsRequired = false,
            IsSensitive = isSensitive,
            Validation = validation,
            Source = source
        };
    }

    private string? GetCurrentValue(string path, bool isSensitive)
    {
        var value = _configuration[path];
        if (value == null)
        {
            return null;
        }

        if (isSensitive)
        {
            return "***";
        }

        return value;
    }

    private string DetermineSource(string path)
    {
        // Check if environment variable is set
        var envVarName = path.Replace(":", "__");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVarName)))
        {
            return "Environment";
        }

        // Check if value exists in configuration
        if (_configuration[path] != null)
        {
            return "appsettings.json";
        }

        return "Default";
    }

    private List<EnvironmentVariableInfo> BuildEnvironmentVariableQuickReference()
    {
        return new List<EnvironmentVariableInfo>
        {
            // Feature flags
            new() { Name = "HONUA_ADMIN_UI", ConfigPath = "Features", Description = "Enable web admin interface", Default = "false", Example = "true" },
            new() { Name = "HONUA_OBSERVABILITY", ConfigPath = "Features", Description = "Enable metrics endpoints", Default = "false", Example = "true" },
            new() { Name = "HONUA_OPENTELEMETRY", ConfigPath = "Features", Description = "Enable distributed tracing", Default = "false", Example = "true" },
            new() { Name = "HONUA_SKIP_MIGRATIONS", ConfigPath = "Features", Description = "Skip database migrations", Default = "false", Example = "true" },
            new() { Name = "HONUA_DEV_AUTH", ConfigPath = "Security", Description = "Development auth bypass", Required = false, Example = "dev-token" },
            new() { Name = "HONUA_ADMIN_PASSWORD", ConfigPath = "Security", Description = "Admin API password", Required = false, Example = "secure-password" },

            // Database
            new() { Name = "ConnectionStrings__DefaultConnection", ConfigPath = "Database", Description = "PostgreSQL connection string", Required = true, Example = "Host=localhost;Database=honua;Username=postgres;Password=password" },

            // Cache
            new() { Name = "Cache__Enabled", ConfigPath = "Cache", Description = "Enable caching", Default = "true", Example = "false" },
            new() { Name = "Cache__DefaultTtlSeconds", ConfigPath = "Cache", Description = "Default cache TTL", Default = "300", Example = "600" },
            new() { Name = "Cache__EnableFallback", ConfigPath = "Cache", Description = "Use in-memory fallback", Default = "true", Example = "false" },

            // Key limits
            new() { Name = "Limits__Query__MaxRecordCount", ConfigPath = "Limits.Query", Description = "Max features per query", Default = "2000", Example = "5000" },
            new() { Name = "Limits__Query__QueryTimeout", ConfigPath = "Limits.Query", Description = "Query timeout", Default = "00:00:30", Example = "00:01:00" },
            new() { Name = "Limits__Geometry__MaxVerticesPerGeometry", ConfigPath = "Limits.Geometry", Description = "Max vertices", Default = "100000", Example = "50000" },
            new() { Name = "Limits__Connections__MaxConcurrentQueries", ConfigPath = "Limits.Connections", Description = "Max concurrent queries", Default = "100", Example = "200" },

            // Rate limiting
            new() { Name = "RateLimit__MaxRequestsPerWindow", ConfigPath = "RateLimit", Description = "Max requests per window", Default = "100", Example = "200" },
            new() { Name = "RateLimit__WindowSize", ConfigPath = "RateLimit", Description = "Rate limit window", Default = "00:01:00", Example = "00:00:30" },

            // CORS
            new() { Name = "Cors__AllowedOrigins__0", ConfigPath = "Security", Description = "First CORS origin", Required = false, Example = "https://myapp.example.com" },
            new() { Name = "Cors__AllowedOrigins__1", ConfigPath = "Security", Description = "Second CORS origin", Required = false, Example = "https://admin.example.com" }
        };
    }
}
