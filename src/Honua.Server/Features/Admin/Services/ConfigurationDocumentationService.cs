// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Abstractions;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Service for building self-documenting configuration metadata.
/// </summary>
public sealed class ConfigurationDocumentationService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IOptions<LimitsOptions> _limitsOptions;
    private readonly IOptions<CacheOptions> _cacheOptions;
    private readonly IOptions<TileOptions> _tileOptions;
    private readonly IOptions<AdaptiveSamplingOptions> _adaptiveSamplingOptions;
    private readonly IOptions<TracingOptions> _tracingOptions;
    private readonly IReadOnlyList<IConfigurationDocumentationContributor> _configurationDocumentationContributors;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationDocumentationService"/> class.
    /// </summary>
    public ConfigurationDocumentationService(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IOptions<LimitsOptions> limitsOptions,
        IOptions<CacheOptions> cacheOptions,
        IOptions<TileOptions> tileOptions,
        IOptions<AdaptiveSamplingOptions> adaptiveSamplingOptions,
        IOptions<TracingOptions> tracingOptions,
        IEnumerable<IConfigurationDocumentationContributor> configurationDocumentationContributors)
    {
        _configuration = configuration;
        _environment = environment;
        _limitsOptions = limitsOptions;
        _cacheOptions = cacheOptions;
        _tileOptions = tileOptions;
        _adaptiveSamplingOptions = adaptiveSamplingOptions;
        _tracingOptions = tracingOptions;
        _configurationDocumentationContributors = configurationDocumentationContributors?.ToArray()
            ?? throw new ArgumentNullException(nameof(configurationDocumentationContributors));
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
            BuildDeploymentSection(),
            BuildFileStorageSection(),
            BuildLimitsQuerySection(),
            BuildLimitsGeometrySection(),
            BuildLimitsEditsSection(),
            BuildLimitsAttachmentsSection(),
            BuildLimitsTilesSection(),
            BuildLimitsConnectionsSection(),
            BuildLimitsImportsSection(),
            BuildNetworkingSection(),
            BuildTileOptionsSection(),
            BuildTemporaryFilesSection(),
            BuildFileUploadSecuritySection(),
            BuildFeatureStreamingSection(),
            BuildFeatureChangeEventsSection(),
            BuildFeatureChangeWebhookSection(),
            BuildManifestApprovalSection(),
            BuildManifestApprovalWebhookSection(),
            BuildGitOpsWatchSection(),
            BuildSecuritySection(),
            BuildTracingSection(),
            BuildAdaptiveSamplingSection()
        };

        foreach (var contributor in _configurationDocumentationContributors)
        {
            sections.AddRange(contributor.GetSections());
        }

        var envVars = BuildEnvironmentVariableQuickReference(sections);
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
                BuildProperty("Database:SecureConnection:Name", "Database__SecureConnection__Name", "string",
                    "Named secure connection to resolve from the registry (uses DefaultConnection for registry access)", null),
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
            Description = "Redis metadata and output caching configuration",
            Properties =
            [
                BuildProperty("ConnectionStrings:redis", "ConnectionStrings__redis", "string",
                    "Redis connection string for metadata/output caching", null, isSensitive: true),
                BuildPropertyWithCurrent("Cache:Enabled", "Cache__Enabled", "boolean",
                    "Whether caching is enabled", true, opts.Enabled),
                BuildPropertyWithCurrent("Cache:ResponseCachingEnabled", "Cache__ResponseCachingEnabled", "boolean",
                    "Whether exact response caching is enabled separately from metadata/catalog caching", false, opts.ResponseCachingEnabled),
                BuildPropertyWithCurrent("Cache:DefaultTtlSeconds", "Cache__DefaultTtlSeconds", "integer",
                    "Default cache TTL in seconds", 1800, opts.DefaultTtlSeconds, "Range: 1-86400"),
                BuildPropertyWithCurrent("Cache:ServiceTtlSeconds", "Cache__ServiceTtlSeconds", "integer",
                    "Service metadata cache TTL in seconds", 3600, opts.ServiceTtlSeconds, "Range: 1-86400"),
                BuildPropertyWithCurrent("Cache:LayerTtlSeconds", "Cache__LayerTtlSeconds", "integer",
                    "Layer metadata cache TTL in seconds", 1800, opts.LayerTtlSeconds, "Range: 1-86400"),
                BuildPropertyWithCurrent("Cache:QueryTtlSeconds", "Cache__QueryTtlSeconds", "integer",
                    "Query response cache TTL in seconds", 30, opts.QueryTtlSeconds, "Range: 1-3600"),
                BuildPropertyWithCurrent("Cache:NegativeTtlSeconds", "Cache__NegativeTtlSeconds", "integer",
                    "Negative cache TTL in seconds for missing layers/services", 60, opts.NegativeTtlSeconds, "Range: 1-3600"),
                BuildPropertyWithCurrent("Cache:JitterPercentage", "Cache__JitterPercentage", "number",
                    "TTL jitter percentage to prevent stampedes", 0.2, opts.JitterPercentage, "Range: 0-0.5"),
                BuildPropertyWithCurrent("Cache:EnableFallback", "Cache__EnableFallback", "boolean",
                    "Use in-memory fallback when Redis unavailable", true, opts.EnableFallback),
                BuildPropertyWithCurrent("Cache:FallbackMaxEntries", "Cache__FallbackMaxEntries", "integer",
                    "Maximum entries in fallback cache", 1000, opts.FallbackMaxEntries, "Range: 10-100000"),
                BuildPropertyWithCurrent("Cache:RetryIntervalSeconds", "Cache__RetryIntervalSeconds", "integer",
                    "Retry interval after Redis failure", 30, opts.RetryIntervalSeconds, "Range: 5-300"),
                BuildPropertyWithCurrent("Cache:KeyPrefix", "Cache__KeyPrefix", "string",
                    "Prefix for cache keys", "honua:", opts.KeyPrefix),
                BuildPropertyWithCurrent("Cache:BackgroundRefreshEnabled", "Cache__BackgroundRefreshEnabled", "boolean",
                    "Enable stale-while-revalidate background refresh for near-expiry entries", true, opts.BackgroundRefreshEnabled),
                BuildPropertyWithCurrent("Cache:BackgroundRefreshThreshold", "Cache__BackgroundRefreshThreshold", "number",
                    "Fraction of TTL remaining that triggers background refresh", 0.25, opts.BackgroundRefreshThreshold, "Range: 0.05-0.75"),
                BuildPropertyWithCurrent("Cache:MaxConcurrentRefreshes", "Cache__MaxConcurrentRefreshes", "integer",
                    "Maximum concurrent background refresh operations", 10, opts.MaxConcurrentRefreshes, "Range: 1-100"),
                BuildPropertyWithCurrent("Cache:RefreshTimeoutSeconds", "Cache__RefreshTimeoutSeconds", "integer",
                    "Timeout per background refresh operation in seconds", 30, opts.RefreshTimeoutSeconds, "Range: 5-120")
            ]
        };
    }

    private ConfigurationSection BuildDeploymentSection()
    {
        return new ConfigurationSection
        {
            Name = "Deployment",
            Description = "Deployment mode configuration for single-instance or multi-node operation",
            Properties =
            [
                BuildProperty("Deployment:Mode", "Deployment__Mode", "string",
                    "Deployment mode (SingleInstance or MultiNode). MultiNode requires Redis and shared file storage.",
                    DeploymentMode.SingleInstance.ToString())
            ]
        };
    }

    private ConfigurationSection BuildFileStorageSection()
    {
        var defaults = new CloudStorageOptions();
        var defaultLocalBasePath = Path.Combine(Path.GetTempPath(), "honua-storage");

        return new ConfigurationSection
        {
            Name = "FileStorage",
            Description = "Cloud file storage configuration for attachments and imports",
            Properties =
            [
                BuildProperty("FileStorage:Provider", "FileStorage__Provider", "string",
                    "Storage provider (Local, AwsS3, AzureBlob)",
                    CloudStorageProvider.Local.ToString()),
                BuildProperty("FileStorage:DefaultTimeToLive", "FileStorage__DefaultTimeToLive", "timespan",
                    "Default time-to-live for temporary files (HH:MM:SS)", defaults.DefaultTimeToLive),
                BuildProperty("FileStorage:MaxFileSizeBytes", "FileStorage__MaxFileSizeBytes", "integer",
                    "Maximum file size allowed for uploads in bytes", defaults.MaxFileSizeBytes),
                BuildProperty("FileStorage:EnableAutomaticCleanup", "FileStorage__EnableAutomaticCleanup", "boolean",
                    "Enable automatic cleanup of expired files", defaults.EnableAutomaticCleanup),
                BuildProperty("FileStorage:CleanupInterval", "FileStorage__CleanupInterval", "timespan",
                    "Interval for cleanup job execution (HH:MM:SS)", defaults.CleanupInterval),
                BuildProperty("FileStorage:LocalStorage:BasePath", "FileStorage__LocalStorage__BasePath", "string",
                    "Base directory for local storage provider", defaultLocalBasePath),
                BuildProperty("FileStorage:LocalStorage:CreateDirectoryIfNotExists", "FileStorage__LocalStorage__CreateDirectoryIfNotExists", "boolean",
                    "Create local storage directory if missing", true),

                BuildProperty("FileStorage:AwsS3:BucketName", "FileStorage__AwsS3__BucketName", "string",
                    "AWS S3 bucket name (required when Provider=AwsS3)", null, validation: "Required when Provider=AwsS3"),
                BuildProperty("FileStorage:AwsS3:Region", "FileStorage__AwsS3__Region", "string",
                    "AWS region (required when Provider=AwsS3)", null, validation: "Required when Provider=AwsS3"),
                BuildProperty("FileStorage:AwsS3:KeyPrefix", "FileStorage__AwsS3__KeyPrefix", "string",
                    "Optional key prefix for stored objects", null),
                BuildProperty("FileStorage:AwsS3:ServiceUrl", "FileStorage__AwsS3__ServiceUrl", "string",
                    "Optional S3-compatible service URL (e.g., Localstack/MinIO)", null),
                BuildProperty("FileStorage:AwsS3:ForcePathStyle", "FileStorage__AwsS3__ForcePathStyle", "boolean",
                    "Force path-style S3 addressing (useful for emulators)", false),
                BuildProperty("FileStorage:AwsS3:AccessKeyId", "FileStorage__AwsS3__AccessKeyId", "string",
                    "AWS access key id (optional if using IAM role)", null, isSensitive: true),
                BuildProperty("FileStorage:AwsS3:SecretAccessKey", "FileStorage__AwsS3__SecretAccessKey", "string",
                    "AWS secret access key (optional if using IAM role)", null, isSensitive: true),
                BuildProperty("FileStorage:AwsS3:EnableServerSideEncryption", "FileStorage__AwsS3__EnableServerSideEncryption", "boolean",
                    "Enable server-side encryption for S3 objects", true),

                BuildProperty("FileStorage:AzureBlob:ConnectionString", "FileStorage__AzureBlob__ConnectionString", "string",
                    "Azure Blob connection string (required when Provider=AzureBlob)", null, isSensitive: true, validation: "Required when Provider=AzureBlob"),
                BuildProperty("FileStorage:AzureBlob:ContainerName", "FileStorage__AzureBlob__ContainerName", "string",
                    "Azure Blob container name (required when Provider=AzureBlob)", null, validation: "Required when Provider=AzureBlob"),
                BuildProperty("FileStorage:AzureBlob:BlobPrefix", "FileStorage__AzureBlob__BlobPrefix", "string",
                    "Optional blob prefix for stored objects", null)
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
                    "Maximum pagination offset", 1000000, opts.MaxOffset, "Range: 0-1000000"),
                BuildPropertyWithCurrent("Limits:Query:MaxBboxAreaSqKm", "Limits__Query__MaxBboxAreaSqKm", "number",
                    "Maximum bounding box area in square km", 100000.0, opts.MaxBboxAreaSqKm),
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
                    "Maximum geometry size in bytes", FileSizeConstants.TenMB, opts.MaxGeometrySize, "Range: 1MB-100MB"),
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
                    "Maximum request body size in bytes", FileSizeConstants.FiftyMB, opts.MaxPayloadSize, "Range: 1MB-500MB")
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
                    "Maximum single attachment size in bytes", FileSizeConstants.TenMB, opts.MaxAttachmentSize, "Range: 1MB-100MB"),
                BuildPropertyWithCurrent("Limits:Attachments:MaxAttachmentsPerFeature", "Limits__Attachments__MaxAttachmentsPerFeature", "integer",
                    "Maximum attachments per feature", 10, opts.MaxAttachmentsPerFeature, "Range: 1-100"),
                BuildPropertyWithCurrent("Limits:Attachments:MaxTotalAttachmentSize", "Limits__Attachments__MaxTotalAttachmentSize", "integer",
                    "Maximum total attachment size per feature in bytes", FileSizeConstants.OneHundredMB, opts.MaxTotalAttachmentSize, "Range: 10MB-1GB"),
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
                    "Maximum features per tile", 10000, opts.MaxFeaturesPerTile, "Range: 1000-1000000"),
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
                    "Maximum database connection pool size", 200, opts.MaxConnectionPoolSize, "Range: 10-500"),
                BuildPropertyWithCurrent("Limits:Connections:MinConnectionPoolSize", "Limits__Connections__MinConnectionPoolSize", "integer",
                    "Minimum database connection pool size (clamped to MaxConnectionPoolSize)", 20, opts.MinConnectionPoolSize, "Range: 0-100"),
                BuildPropertyWithCurrent("Limits:Connections:BufferSizeBytes", "Limits__Connections__BufferSizeBytes", "integer",
                    "Npgsql read/write buffer size in bytes", 32768, opts.BufferSizeBytes, "Range: 4096-65536"),
                BuildPropertyWithCurrent("Limits:Connections:Multiplexing", "Limits__Connections__Multiplexing", "string",
                    "Npgsql multiplexing mode ('auto', 'true', or 'false'); default off avoids write-lock contention at high concurrency", "false", opts.Multiplexing, "Allowed: auto|true|false"),
                BuildPropertyWithCurrent("Limits:Connections:ConnectionAcquisitionTimeoutSeconds", "Limits__Connections__ConnectionAcquisitionTimeoutSeconds", "integer",
                    "Maximum seconds to wait for a gate slot before returning HTTP 503 with Retry-After", 5, opts.ConnectionAcquisitionTimeoutSeconds, "Range: 1-60"),
                BuildPropertyWithCurrent("Limits:Connections:AdaptiveConcurrencyEnabled", "Limits__Connections__AdaptiveConcurrencyEnabled", "boolean",
                    "Enable adaptive query admission below the configured database pool ceiling", false, opts.AdaptiveConcurrencyEnabled, "Allowed: true|false"),
                BuildPropertyWithCurrent("Limits:Connections:AdaptiveConcurrencyMinQueries", "Limits__Connections__AdaptiveConcurrencyMinQueries", "integer",
                    "Minimum adaptive concurrent query limit", 1, opts.AdaptiveConcurrencyMinQueries, "Range: 1-1000"),
                BuildPropertyWithCurrent("Limits:Connections:AdaptiveConcurrencyInitialQueries", "Limits__Connections__AdaptiveConcurrencyInitialQueries", "integer",
                    "Initial adaptive concurrent query limit; 0 starts at MaxConcurrentQueries", 0, opts.AdaptiveConcurrencyInitialQueries, "Range: 0-1000"),
                BuildPropertyWithCurrent("Limits:Connections:AdaptiveConcurrencyMaxQueries", "Limits__Connections__AdaptiveConcurrencyMaxQueries", "integer",
                    "Maximum adaptive concurrent query limit; 0 uses MaxConcurrentQueries", 0, opts.AdaptiveConcurrencyMaxQueries, "Range: 0-1000"),
                BuildPropertyWithCurrent("Limits:Connections:AdaptiveConcurrencyTargetDurationMs", "Limits__Connections__AdaptiveConcurrencyTargetDurationMs", "integer",
                    "Target database lease duration for adaptive admission", 100, opts.AdaptiveConcurrencyTargetDurationMs, "Range: 1-60000"),
                BuildPropertyWithCurrent("Limits:Connections:AdaptiveConcurrencyUpdateIntervalMs", "Limits__Connections__AdaptiveConcurrencyUpdateIntervalMs", "integer",
                    "Minimum milliseconds between adaptive admission adjustments", 1000, opts.AdaptiveConcurrencyUpdateIntervalMs, "Range: 0-60000"),
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
                    "Maximum file size for preview in bytes", FileSizeConstants.TenMB, opts.MaxPreviewSize, "Range: 1MB-50MB"),
                BuildPropertyWithCurrent("Limits:Imports:MaxSyncImportSize", "Limits__Imports__MaxSyncImportSize", "integer",
                    "Maximum file size for sync import in bytes", FileSizeConstants.FiftyMB, opts.MaxSyncImportSize, "Range: 10MB-500MB"),
                BuildPropertyWithCurrent("Limits:Imports:MaxImportSize", "Limits__Imports__MaxImportSize", "integer",
                    "Maximum import file size in bytes", FileSizeConstants.FiveHundredMB, opts.MaxImportSize, "Range: 50MB-5GB"),
                BuildPropertyWithCurrent("Limits:Imports:MaxPreviewFeatures", "Limits__Imports__MaxPreviewFeatures", "integer",
                    "Maximum features in preview", 100, opts.MaxPreviewFeatures, "Range: 10-1000"),
                BuildPropertyWithCurrent("Limits:Imports:MaxPreviewCountScan", "Limits__Imports__MaxPreviewCountScan", "integer",
                    "Maximum features scanned while deriving preview counts for streaming formats with unknown totals", 100000, opts.MaxPreviewCountScan, "Range: 10-1000000"),
                BuildPropertyWithCurrent("Limits:Imports:BatchSize", "Limits__Imports__BatchSize", "integer",
                    "Feature insertion batch size", 1000, opts.BatchSize, "Range: 100-10000")
            ]
        };
    }

    private ConfigurationSection BuildNetworkingSection()
    {
        return new ConfigurationSection
        {
            Name = "Networking",
            Description = "Public URL and proxy forwarding configuration",
            Properties =
            [
                BuildProperty("Public:BaseUrl", "PUBLIC_BASE_URL", "string",
                    "Public base URL used for link generation", null),
                BuildProperty("HostValidation:Enabled", "HostValidation__Enabled", "boolean",
                    "Enable Host header validation middleware", null),
                BuildProperty("HostValidation:AllowedHosts:0", "HostValidation__AllowedHosts__0", "string",
                    "First explicitly allowed host for Host header validation", null),
                BuildProperty("HostValidation:RequireExplicitHosts", "HostValidation__RequireExplicitHosts", "boolean",
                    "Fail startup in non-development when host validation is enabled without explicit hosts or PUBLIC_BASE_URL", false),
                BuildProperty("ForwardedHeaders:Enabled", "ForwardedHeaders__Enabled", "boolean",
                    "Enable forwarded headers processing", false),
                BuildProperty("ForwardedHeaders:ForwardLimit", "ForwardedHeaders__ForwardLimit", "integer",
                    "Maximum number of forwarded entries to process", 1),
                BuildProperty("ForwardedHeaders:KnownProxies", "ForwardedHeaders__KnownProxies__0", "string",
                    "Trusted proxy IP addresses for forwarded headers", null)
            ]
        };
    }

    private ConfigurationSection BuildTileOptionsSection()
    {
        var opts = _tileOptions.Value;
        return new ConfigurationSection
        {
            Name = "TileOptions",
            Description = "Tile rendering and caching options (limits are under Limits:Tiles)",
            Properties =
            [
                BuildPropertyWithCurrent("TileOptions:SimplifyZoom", "TileOptions__SimplifyZoom", "integer",
                    "Zoom level below which geometries are simplified", 10, opts.SimplifyZoom),
                BuildPropertyWithCurrent("TileOptions:CacheMaxAge", "TileOptions__CacheMaxAge", "integer",
                    "Cache control max-age in seconds", 3600, opts.CacheMaxAge),
                BuildPropertyWithCurrent("TileOptions:TileExtent", "TileOptions__TileExtent", "integer",
                    "MVT tile extent", 4096, opts.TileExtent),
                BuildPropertyWithCurrent("TileOptions:TileBuffer", "TileOptions__TileBuffer", "integer",
                    "MVT buffer size in pixels", 256, opts.TileBuffer)
            ]
        };
    }

    private ConfigurationSection BuildTemporaryFilesSection()
    {
        return new ConfigurationSection
        {
            Name = "TemporaryFiles",
            Description = "Temporary export and artifact file storage configuration. Shared cloud-backed temporary files require Redis coordination so quotas remain correct across replicas.",
            Properties =
            [
                BuildProperty("TemporaryFiles:StorageDirectory", "TemporaryFiles__StorageDirectory", "string",
                    "Base directory for temporary file storage", Path.Combine(Path.GetTempPath(), "honua-temp")),
                BuildProperty("TemporaryFiles:DefaultExpiration", "TemporaryFiles__DefaultExpiration", "timespan",
                    "Default expiration time for temporary files", TimeSpan.FromHours(1)),
                BuildProperty("TemporaryFiles:MaxFileSizeBytes", "TemporaryFiles__MaxFileSizeBytes", "integer",
                    "Maximum temporary file size in bytes", 50L * 1024 * 1024),
                BuildProperty("TemporaryFiles:MaxTotalStorageBytes", "TemporaryFiles__MaxTotalStorageBytes", "integer",
                    "Maximum aggregate temporary storage in bytes", 500L * 1024 * 1024),
                BuildProperty("TemporaryFiles:MaxFileCount", "TemporaryFiles__MaxFileCount", "integer",
                    "Maximum number of active temporary files", 5000),
                BuildProperty("TemporaryFiles:StorageFullRetryAfterSeconds", "TemporaryFiles__StorageFullRetryAfterSeconds", "integer",
                    "Retry-After hint when temporary storage is saturated", 60),
                BuildProperty("TemporaryFiles:BaseUrl", "TemporaryFiles__BaseUrl", "string",
                    "Optional absolute base URL used when serving temporary files", null)
            ]
        };
    }

    private ConfigurationSection BuildFileUploadSecuritySection()
    {
        return new ConfigurationSection
        {
            Name = "FileUploadSecurity",
            Description = "Security scanning limits for uploaded files",
            Properties =
            [
                BuildProperty("FileUploadSecurity:MaxSecurityScanSizeBytes", "FileUploadSecurity__MaxSecurityScanSizeBytes", "integer",
                    "Maximum prefix bytes to inspect for binary signatures before full text-format scanning", 10 * 1024 * 1024)
            ]
        };
    }

    private ConfigurationSection BuildFeatureStreamingSection()
    {
        return new ConfigurationSection
        {
            Name = "FeatureStreaming",
            Description = "Real-time feature-change streaming transport configuration",
            Properties =
            [
                BuildProperty("FeatureStreaming:HeartbeatInterval", "FeatureStreaming__HeartbeatInterval", "timespan",
                    "Interval between heartbeat frames sent to connected clients", TimeSpan.FromSeconds(30)),
                BuildProperty("FeatureStreaming:MaxBufferPerConnection", "FeatureStreaming__MaxBufferPerConnection", "integer",
                    "Maximum queued messages per connection before a slow consumer is disconnected", 256),
                BuildProperty("FeatureStreaming:MaxConcurrentSessions", "FeatureStreaming__MaxConcurrentSessions", "integer",
                    "Maximum number of concurrently connected feature-stream sessions", 256),
                BuildProperty("FeatureStreaming:ReplayBatchSize", "FeatureStreaming__ReplayBatchSize", "integer",
                    "Number of events fetched per batch during cursor replay", 200),
                BuildProperty("FeatureStreaming:CrossNodeSyncInterval", "FeatureStreaming__CrossNodeSyncInterval", "timespan",
                    "Interval between shared-store sweeps for cross-node event pickup", TimeSpan.FromSeconds(1))
            ]
        };
    }

    private ConfigurationSection BuildFeatureChangeEventsSection()
    {
        return new ConfigurationSection
        {
            Name = "FeatureChangeEvents",
            Description = "Feature-change event retention and replay configuration",
            Properties =
            [
                BuildProperty("FeatureChangeEvents:MaxRetainedEvents", "FeatureChangeEvents__MaxRetainedEvents", "integer",
                    "Maximum number of retained events for replay", 20_000)
            ]
        };
    }

    private ConfigurationSection BuildFeatureChangeWebhookSection()
    {
        return new ConfigurationSection
        {
            Name = "FeatureChangeEvents.Webhook",
            Description = "Outbound webhook delivery for feature-change events",
            Properties =
            [
                BuildProperty("FeatureChangeEvents:Webhook:Enabled", "FeatureChangeEvents__Webhook__Enabled", "boolean",
                    "Enable outbound webhook delivery for feature-change events", false),
                BuildProperty("FeatureChangeEvents:Webhook:Url", "FeatureChangeEvents__Webhook__Url", "string",
                    "Absolute HTTPS webhook URL", null),
                BuildProperty("FeatureChangeEvents:Webhook:Secret", "FeatureChangeEvents__Webhook__Secret", "string",
                    "Shared HMAC secret for webhook signatures", null, isSensitive: true),
                BuildProperty("FeatureChangeEvents:Webhook:MaxAttempts", "FeatureChangeEvents__Webhook__MaxAttempts", "integer",
                    "Maximum delivery attempts per event", 5),
                BuildProperty("FeatureChangeEvents:Webhook:InitialBackoffMs", "FeatureChangeEvents__Webhook__InitialBackoffMs", "integer",
                    "Initial webhook retry backoff in milliseconds", 500),
                BuildProperty("FeatureChangeEvents:Webhook:MaxBackoffMs", "FeatureChangeEvents__Webhook__MaxBackoffMs", "integer",
                    "Maximum webhook retry backoff in milliseconds", 30_000),
                BuildProperty("FeatureChangeEvents:Webhook:RequestTimeoutSeconds", "FeatureChangeEvents__Webhook__RequestTimeoutSeconds", "integer",
                    "Per-request webhook timeout in seconds", 15)
            ]
        };
    }

    private ConfigurationSection BuildManifestApprovalSection()
    {
        return new ConfigurationSection
        {
            Name = "ManifestApproval",
            Description = "Approval workflow configuration for control-plane manifest changes",
            Properties =
            [
                BuildProperty("ManifestApproval:Enabled", "ManifestApproval__Enabled", "boolean",
                    "Enable manifest approval workflows", false),
                BuildProperty("ManifestApproval:DefaultTimeoutMinutes", "ManifestApproval__DefaultTimeoutMinutes", "integer",
                    "Default approval timeout in minutes", 1440),
                BuildProperty("ManifestApproval:ExpiryScanIntervalSeconds", "ManifestApproval__ExpiryScanIntervalSeconds", "integer",
                    "Background scan interval for expiring pending approvals", 60)
            ]
        };
    }

    private ConfigurationSection BuildManifestApprovalWebhookSection()
    {
        return new ConfigurationSection
        {
            Name = "ManifestApproval.Webhook",
            Description = "Outbound webhook delivery for manifest approval events",
            Properties =
            [
                BuildProperty("ManifestApproval:Webhook:Enabled", "ManifestApproval__Webhook__Enabled", "boolean",
                    "Enable outbound webhook delivery for approval events", false),
                BuildProperty("ManifestApproval:Webhook:Url", "ManifestApproval__Webhook__Url", "string",
                    "Absolute HTTPS webhook URL", null),
                BuildProperty("ManifestApproval:Webhook:Secret", "ManifestApproval__Webhook__Secret", "string",
                    "Shared HMAC secret for webhook signatures", null, isSensitive: true),
                BuildProperty("ManifestApproval:Webhook:MaxAttempts", "ManifestApproval__Webhook__MaxAttempts", "integer",
                    "Maximum delivery attempts per event", 5),
                BuildProperty("ManifestApproval:Webhook:InitialBackoffMs", "ManifestApproval__Webhook__InitialBackoffMs", "integer",
                    "Initial webhook retry backoff in milliseconds", 500),
                BuildProperty("ManifestApproval:Webhook:MaxBackoffMs", "ManifestApproval__Webhook__MaxBackoffMs", "integer",
                    "Maximum webhook retry backoff in milliseconds", 30_000),
                BuildProperty("ManifestApproval:Webhook:RequestTimeoutSeconds", "ManifestApproval__Webhook__RequestTimeoutSeconds", "integer",
                    "Per-request webhook timeout in seconds", 15)
            ]
        };
    }

    private ConfigurationSection BuildGitOpsWatchSection()
    {
        return new ConfigurationSection
        {
            Name = "GitOpsWatch",
            Description = "GitOps repository watch configuration",
            Properties =
            [
                BuildProperty("GitOpsWatch:Enabled", "GitOpsWatch__Enabled", "boolean",
                    "Enable GitOps repository watch functionality", false),
                BuildProperty("GitOpsWatch:MinPollIntervalSeconds", "GitOpsWatch__MinPollIntervalSeconds", "integer",
                    "Minimum allowed poll interval in seconds", 30)
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
                BuildProperty("HONUA_ADMIN_UI_CORS_ORIGINS", "HONUA_ADMIN_UI_CORS_ORIGINS", "string",
                    "Comma-separated allowed origins for standalone Admin UI", null),
                BuildProperty("Authentication:BasicCompatibility:Enabled", "HONUA_ENABLE_BASIC_AUTH_COMPAT", "boolean",
                    "Enable HTTP Basic authentication compatibility mode", false),
                BuildProperty("Authentication:BasicCompatibility:RequireHttps", "HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH", "boolean",
                    "Require HTTPS when HTTP Basic authentication compatibility is enabled", true),
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

    private ConfigurationSection BuildTracingSection()
    {
        var options = _tracingOptions.Value;
        return new ConfigurationSection
        {
            Name = "Tracing",
            Description = "OpenTelemetry distributed tracing configuration",
            Properties =
            [
                BuildProperty("Tracing:Enabled", "HONUA__TRACING__ENABLED", "boolean",
                    "Enable distributed tracing", options.Enabled),
                BuildProperty("Tracing:SamplingRatio", "HONUA__TRACING__SAMPLINGRATIO", "decimal",
                    "Static sampling ratio (0.0 to 1.0)", options.SamplingRatio),
                BuildProperty("Tracing:IncludeDbStatementText", "HONUA__TRACING__INCLUDEDBSTATEMENTTEXT", "boolean",
                    "Include database query text in spans", options.IncludeDbStatementText),
                BuildProperty("Tracing:TraceHealthEndpoints", "HONUA__TRACING__TRACEHEALTHENDPOINTS", "boolean",
                    "Trace health check endpoints", options.TraceHealthEndpoints),
                BuildProperty("Tracing:RecordExceptionStackTraces", "HONUA__TRACING__RECORDEXCEPTIONSTACKTRACES", "boolean",
                    "Record exception stack traces in spans", options.RecordExceptionStackTraces),
                BuildProperty("Tracing:OtlpEndpoint", "HONUA__TRACING__OTLPENDPOINT", "string",
                    "OTLP exporter endpoint URL", options.OtlpEndpoint ?? ""),
                BuildProperty("Tracing:OtlpHeaders", "HONUA__TRACING__OTLPHEADERS", "string",
                    "OTLP exporter headers", options.OtlpHeaders ?? "", isSensitive: true)
            ]
        };
    }

    private ConfigurationSection BuildAdaptiveSamplingSection()
    {
        var options = _adaptiveSamplingOptions.Value;
        return new ConfigurationSection
        {
            Name = "AdaptiveSampling",
            Description = "Intelligent adaptive sampling for distributed tracing - automatically adjusts sampling rates based on system load and error rates",
            Properties =
            [
                BuildProperty("AdaptiveSampling:Enabled", "HONUA__ADAPTIVESAMPLING__ENABLED", "boolean",
                    "Enable adaptive sampling (when disabled, uses static Tracing:SamplingRatio)", options.Enabled),
                BuildProperty("AdaptiveSampling:BaseSamplingRate", "HONUA__ADAPTIVESAMPLING__BASESAMPLINGRATE", "decimal",
                    "Base sampling rate used as starting point (0.001 to 1.0)", options.BaseSamplingRate),
                BuildProperty("AdaptiveSampling:MinSamplingRate", "HONUA__ADAPTIVESAMPLING__MINSAMPLINGRATE", "decimal",
                    "Minimum sampling rate under high load (0.001 to 0.5)", options.MinSamplingRate),
                BuildProperty("AdaptiveSampling:MaxSamplingRate", "HONUA__ADAPTIVESAMPLING__MAXSAMPLINGRATE", "decimal",
                    "Maximum sampling rate during errors/low load (0.1 to 1.0)", options.MaxSamplingRate),
                BuildProperty("AdaptiveSampling:EvaluationWindow", "HONUA__ADAPTIVESAMPLING__EVALUATIONWINDOW", "timespan",
                    "How often to adjust sampling rates (format: hh:mm:ss)", options.EvaluationWindow.ToString()),
                BuildProperty("AdaptiveSampling:Load:CpuThreshold", "HONUA__ADAPTIVESAMPLING__LOAD__CPUTHRESHOLD", "decimal",
                    "CPU usage % threshold for reducing sampling (30-95)", options.Load.CpuThreshold),
                BuildProperty("AdaptiveSampling:Load:MemoryThreshold", "HONUA__ADAPTIVESAMPLING__LOAD__MEMORYTHRESHOLD", "decimal",
                    "Memory usage % threshold for reducing sampling (30-95)", options.Load.MemoryThreshold),
                BuildProperty("AdaptiveSampling:Load:ActiveRequestThreshold", "HONUA__ADAPTIVESAMPLING__LOAD__ACTIVEREQUESTTHRESHOLD", "integer",
                    "Active request count threshold (10-1000)", options.Load.ActiveRequestThreshold),
                BuildProperty("AdaptiveSampling:Load:ResponseTimeThresholdMs", "HONUA__ADAPTIVESAMPLING__LOAD__RESPONSETIMETHRESHOLDMS", "integer",
                    "Response time threshold in ms (100-10000)", options.Load.ResponseTimeThresholdMs),
                BuildProperty("AdaptiveSampling:Error:ErrorRateThreshold", "HONUA__ADAPTIVESAMPLING__ERROR__ERRORRATETHRESHOLD", "decimal",
                    "Error rate % that triggers increased sampling (0.1-50)", options.Error.ErrorRateThreshold),
                BuildProperty("AdaptiveSampling:Error:ErrorMultiplier", "HONUA__ADAPTIVESAMPLING__ERROR__ERRORMULTIPLIER", "decimal",
                    "Multiplier for sampling during errors (1.5-10)", options.Error.ErrorMultiplier),
                BuildProperty("AdaptiveSampling:Error:ErrorWindowMinutes", "HONUA__ADAPTIVESAMPLING__ERROR__ERRORWINDOWMINUTES", "integer",
                    "Time window for error rate calculation in minutes (1-30)", options.Error.ErrorWindowMinutes),
                BuildProperty("AdaptiveSampling:Operations:Enabled", "HONUA__ADAPTIVESAMPLING__OPERATIONS__ENABLED", "boolean",
                    "Enable operation-specific sampling rates", options.Operations.Enabled),
                BuildProperty("AdaptiveSampling:Operations:CriticalRate", "HONUA__ADAPTIVESAMPLING__OPERATIONS__CRITICALRATE", "decimal",
                    "Sampling rate for critical operations (auth, data writes) (0.1-1.0)", options.Operations.CriticalRate),
                BuildProperty("AdaptiveSampling:Operations:ImportantRate", "HONUA__ADAPTIVESAMPLING__OPERATIONS__IMPORTANTRATE", "decimal",
                    "Sampling rate for important operations (spatial queries) (0.05-1.0)", options.Operations.ImportantRate),
                BuildProperty("AdaptiveSampling:Operations:NormalRate", "HONUA__ADAPTIVESAMPLING__OPERATIONS__NORMALRATE", "decimal",
                    "Sampling rate for normal operations (standard queries) (0.01-1.0)", options.Operations.NormalRate),
                BuildProperty("AdaptiveSampling:Operations:BackgroundRate", "HONUA__ADAPTIVESAMPLING__OPERATIONS__BACKGROUNDRATE", "decimal",
                    "Sampling rate for background operations (health checks) (0.001-0.1)", options.Operations.BackgroundRate)
            ]
        };
    }

    private ConfigurationProperty BuildProperty(string path, string envVar, string type, string description,
        object? defaultValue, bool isRequired = false, bool isSensitive = false, string? validation = null)
    {
        var currentValue = GetCurrentValue(path, envVar, isSensitive);
        var source = DetermineSource(path, envVar);

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
        var source = DetermineSource(path, envVar);
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

    private string? GetCurrentValue(string path, string envVar, bool isSensitive)
    {
        var value = GetResolvedValue(path, envVar);
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

    private string DetermineSource(string path, string envVar)
    {
        if (!string.IsNullOrEmpty(_configuration[envVar]) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
        {
            return "Environment";
        }

        var normalizedEnvVarName = path.Replace(":", "__");
        if (!string.IsNullOrEmpty(_configuration[normalizedEnvVarName]) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(normalizedEnvVarName)))
        {
            return "Environment";
        }

        if (_configuration[path] != null)
        {
            return "appsettings.json";
        }

        return "Default";
    }

    private string? GetResolvedValue(string path, string envVar) =>
        _configuration[envVar]
        ?? _configuration[path.Replace(":", "__")]
        ?? _configuration[path];

    private List<EnvironmentVariableInfo> BuildEnvironmentVariableQuickReference(IEnumerable<ConfigurationSection> sections)
    {
        var envVars = new List<EnvironmentVariableInfo>
        {
            // Feature flags
            new() { Name = "HONUA_OBSERVABILITY", ConfigPath = "Features", Description = "Enable metrics endpoints", Default = "false", Example = "true" },
            new() { Name = "HONUA_OPENTELEMETRY", ConfigPath = "Features", Description = "Enable distributed tracing", Default = "false", Example = "true" },
            new() { Name = "HONUA_SKIP_MIGRATIONS", ConfigPath = "Features", Description = "Skip database migrations", Default = "false", Example = "true" },
            new() { Name = "HONUA_DEV_AUTH", ConfigPath = "Security", Description = "Development auth bypass", Required = false, Example = "dev-token" },
            new() { Name = "HONUA_ADMIN_PASSWORD", ConfigPath = "Security", Description = "Admin API password", Required = false, Example = "secure-password" },
            new() { Name = "HONUA_ADMIN_UI_CORS_ORIGINS", ConfigPath = "Security", Description = "Standalone Admin UI origins", Required = false, Example = "https://admin.example.com" },

            // Database
            new() { Name = "ConnectionStrings__DefaultConnection", ConfigPath = "Database", Description = "PostgreSQL connection string", Required = true, Example = "Host=localhost;Database=honua;Username=postgres;Password=password" },
            new() { Name = "Database__SecureConnection__Name", ConfigPath = "Database", Description = "Named secure connection to use from registry", Required = false, Example = "production-primary" },

            // Cache
            new() { Name = "ConnectionStrings__redis", ConfigPath = "Cache", Description = "Redis connection string for metadata/output caching", Required = false, Example = "localhost:6379" },
            new() { Name = "Cache__Enabled", ConfigPath = "Cache", Description = "Enable caching", Default = "true", Example = "false" },
            new() { Name = "Cache__ResponseCachingEnabled", ConfigPath = "Cache", Description = "Enable exact response caching separately from metadata/catalog caching", Default = "false", Example = "true" },
            new() { Name = "Cache__DefaultTtlSeconds", ConfigPath = "Cache", Description = "Default cache TTL", Default = "1800", Example = "600" },
            new() { Name = "Cache__ServiceTtlSeconds", ConfigPath = "Cache", Description = "Service metadata cache TTL", Default = "3600", Example = "1800" },
            new() { Name = "Cache__LayerTtlSeconds", ConfigPath = "Cache", Description = "Layer metadata cache TTL", Default = "1800", Example = "900" },
            new() { Name = "Cache__QueryTtlSeconds", ConfigPath = "Cache", Description = "Query response cache TTL", Default = "30", Example = "60" },
            new() { Name = "Cache__NegativeTtlSeconds", ConfigPath = "Cache", Description = "Negative cache TTL for missing resources", Default = "60", Example = "30" },
            new() { Name = "Cache__JitterPercentage", ConfigPath = "Cache", Description = "TTL jitter percentage", Default = "0.2", Example = "0.1" },
            new() { Name = "Cache__EnableFallback", ConfigPath = "Cache", Description = "Use in-memory fallback", Default = "true", Example = "false" },
            new() { Name = "Cache__BackgroundRefreshEnabled", ConfigPath = "Cache", Description = "Enable stale-while-revalidate background refresh", Default = "true", Example = "false" },
            new() { Name = "Cache__BackgroundRefreshThreshold", ConfigPath = "Cache", Description = "TTL fraction triggering background refresh", Default = "0.25", Example = "0.3" },
            new() { Name = "Cache__MaxConcurrentRefreshes", ConfigPath = "Cache", Description = "Max concurrent background refreshes", Default = "10", Example = "20" },
            new() { Name = "Cache__RefreshTimeoutSeconds", ConfigPath = "Cache", Description = "Background refresh timeout", Default = "30", Example = "60" },

            // Deployment
            new() { Name = "Deployment__Mode", ConfigPath = "Deployment", Description = "Deployment mode (SingleInstance or MultiNode)", Default = "SingleInstance", Example = "MultiNode" },

            // File storage
            new() { Name = "FileStorage__Provider", ConfigPath = "FileStorage", Description = "Storage provider (Local, AwsS3, AzureBlob)", Default = "Local", Example = "AwsS3" },
            new() { Name = "HONUA_STORAGE_PROVIDER", ConfigPath = "FileStorage", Description = "Storage provider override (env alias)", Required = false, Example = "AzureBlob" },
            new() { Name = "FileStorage__LocalStorage__BasePath", ConfigPath = "FileStorage", Description = "Local storage base path", Required = false, Example = "/var/lib/honua/storage" },
            new() { Name = "FileStorage__AwsS3__BucketName", ConfigPath = "FileStorage", Description = "AWS S3 bucket name", Required = false, Example = "honua-prod" },
            new() { Name = "FileStorage__AwsS3__Region", ConfigPath = "FileStorage", Description = "AWS S3 region", Required = false, Example = "us-west-2" },
            new() { Name = "FileStorage__AwsS3__ServiceUrl", ConfigPath = "FileStorage", Description = "S3-compatible service URL (Localstack/MinIO)", Required = false, Example = "http://localhost:4566" },
            new() { Name = "FileStorage__AwsS3__ForcePathStyle", ConfigPath = "FileStorage", Description = "Force path-style S3 addressing", Required = false, Example = "true" },
            new() { Name = "FileStorage__AzureBlob__ContainerName", ConfigPath = "FileStorage", Description = "Azure Blob container name", Required = false, Example = "honua-attachments" },

            // Key limits
            new() { Name = "Limits__Query__MaxRecordCount", ConfigPath = "Limits.Query", Description = "Max features per query", Default = "2000", Example = "5000" },
            new() { Name = "Limits__Query__QueryTimeout", ConfigPath = "Limits.Query", Description = "Query timeout", Default = "00:00:30", Example = "00:01:00" },
            new() { Name = "Limits__Geometry__MaxVerticesPerGeometry", ConfigPath = "Limits.Geometry", Description = "Max vertices", Default = "100000", Example = "50000" },
            new() { Name = "Limits__Connections__MaxConcurrentQueries", ConfigPath = "Limits.Connections", Description = "Max concurrent queries", Default = "100", Example = "200" },

            // Networking
            new() { Name = "PUBLIC_BASE_URL", ConfigPath = "Networking", Description = "Public base URL override", Required = false, Example = "https://api.honua.example.com" },
            new() { Name = "HostValidation__Enabled", ConfigPath = "Networking", Description = "Enable Host header validation middleware", Default = "true (non-development)", Example = "true" },
            new() { Name = "HostValidation__AllowedHosts__0", ConfigPath = "Networking", Description = "First explicit allowed host for Host header validation", Required = false, Example = "api.honua.example.com" },
            new() { Name = "HostValidation__RequireExplicitHosts", ConfigPath = "Networking", Description = "Enforce explicit hosts/Public:BaseUrl at startup when host validation is enabled", Default = "false", Example = "true" },
            new() { Name = "ForwardedHeaders__Enabled", ConfigPath = "Networking", Description = "Enable forwarded headers", Default = "false", Example = "true" },
            new() { Name = "ForwardedHeaders__ForwardLimit", ConfigPath = "Networking", Description = "Forwarded headers limit", Default = "1", Example = "2" },
            new() { Name = "ForwardedHeaders__KnownProxies__0", ConfigPath = "Networking", Description = "First trusted proxy IP", Required = false, Example = "10.0.0.10" },

            // File storage secrets
            new() { Name = "FileStorage__AwsS3__AccessKeyId", ConfigPath = "FileStorage", Description = "AWS S3 access key id", Required = false, Example = "env:HONUA_S3_KEY_ID" },
            new() { Name = "FileStorage__AwsS3__SecretAccessKey", ConfigPath = "FileStorage", Description = "AWS S3 secret access key", Required = false, Example = "env:HONUA_S3_SECRET" },
            new() { Name = "FileStorage__AzureBlob__ConnectionString", ConfigPath = "FileStorage", Description = "Azure Blob connection string", Required = false, Example = "env:HONUA_AZURE_BLOB_CONN" },

            // Monitoring secrets
            new() { Name = "Monitoring__IntelligentAlerting__NotificationChannels__Email__Password", ConfigPath = "Monitoring", Description = "SMTP password", Required = false, Example = "env:HONUA_SMTP_PASSWORD" },
            new() { Name = "Monitoring__IntelligentAlerting__NotificationChannels__Slack__WebhookUrl", ConfigPath = "Monitoring", Description = "Slack webhook URL", Required = false, Example = "env:HONUA_SLACK_WEBHOOK" },
            new() { Name = "Monitoring__IntelligentAlerting__NotificationChannels__Webhook__Url", ConfigPath = "Monitoring", Description = "Alert webhook URL", Required = false, Example = "env:HONUA_ALERT_WEBHOOK" },
            new() { Name = "Monitoring__IntelligentAlerting__NotificationChannels__Webhook__Headers__Authorization", ConfigPath = "Monitoring", Description = "Alert webhook auth header", Required = false, Example = "env:HONUA_ALERT_WEBHOOK_AUTH" },
            new() { Name = "Monitoring__IntelligentAlerting__NotificationChannels__Sms__ApiKey", ConfigPath = "Monitoring", Description = "SMS API key", Required = false, Example = "env:HONUA_SMS_API_KEY" },

            // CORS
            new() { Name = "Cors__AllowedOrigins__0", ConfigPath = "Security", Description = "First CORS origin", Required = false, Example = "https://myapp.example.com" },
            new() { Name = "Cors__AllowedOrigins__1", ConfigPath = "Security", Description = "Second CORS origin", Required = false, Example = "https://admin.example.com" },

            // Tracing
            new() { Name = "HONUA__TRACING__ENABLED", ConfigPath = "Tracing", Description = "Enable OpenTelemetry tracing", Default = "true", Example = "false" },
            new() { Name = "HONUA__TRACING__SAMPLINGRATIO", ConfigPath = "Tracing", Description = "Static sampling ratio (0.0-1.0)", Default = "0.1", Example = "0.05" },
            new() { Name = "HONUA__TRACING__OTLPENDPOINT", ConfigPath = "Tracing", Description = "OTLP exporter endpoint", Required = false, Example = "http://jaeger:4317" },

            // Adaptive Sampling
            new() { Name = "HONUA__ADAPTIVESAMPLING__ENABLED", ConfigPath = "AdaptiveSampling", Description = "Enable adaptive sampling", Default = "true", Example = "false" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__BASESAMPLINGRATE", ConfigPath = "AdaptiveSampling", Description = "Base sampling rate", Default = "0.1 in shipped appsettings.json; 0.01 if the section is absent", Example = "0.05" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__MINSAMPLINGRATE", ConfigPath = "AdaptiveSampling", Description = "Minimum sampling rate", Default = "0.01 in shipped appsettings.json; 0.001 if the section is absent", Example = "0.005" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__MAXSAMPLINGRATE", ConfigPath = "AdaptiveSampling", Description = "Maximum sampling rate", Default = "0.5 in shipped appsettings.json; 1.0 if the section is absent", Example = "0.8" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__LOAD__CPUTHRESHOLD", ConfigPath = "AdaptiveSampling.Load", Description = "CPU threshold for load reduction", Default = "70", Example = "80" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__LOAD__MEMORYTHRESHOLD", ConfigPath = "AdaptiveSampling.Load", Description = "Memory threshold for load reduction", Default = "80", Example = "90" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__LOAD__ACTIVEREQUESTTHRESHOLD", ConfigPath = "AdaptiveSampling.Load", Description = "Active request threshold for load reduction", Default = "50 in shipped appsettings.json; 100 if the section is absent", Example = "100" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__ERROR__ERRORRATETHRESHOLD", ConfigPath = "AdaptiveSampling.Error", Description = "Error rate % that increases sampling", Default = "5.0", Example = "10.0" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__ERROR__ERRORMULTIPLIER", ConfigPath = "AdaptiveSampling.Error", Description = "Sampling multiplier during errors", Default = "3.0 in shipped appsettings.json; 2.0 if the section is absent", Example = "5.0" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__OPERATIONS__CRITICALRATE", ConfigPath = "AdaptiveSampling.Operations", Description = "Critical operation sampling rate", Default = "1.0", Example = "0.8" },
            new() { Name = "HONUA__ADAPTIVESAMPLING__OPERATIONS__NORMALRATE", ConfigPath = "AdaptiveSampling.Operations", Description = "Normal operation sampling rate", Default = "0.1", Example = "0.05" }
        };

        var seenNames = new HashSet<string>(envVars.Select(static envVar => envVar.Name), StringComparer.Ordinal);

        foreach (var contributor in _configurationDocumentationContributors)
        {
            foreach (var envVar in contributor.GetEnvironmentVariables())
            {
                if (seenNames.Add(envVar.Name))
                {
                    envVars.Add(envVar);
                }
            }
        }

        foreach (var (section, property) in EnumerateProperties(sections))
        {
            if (string.IsNullOrWhiteSpace(property.EnvironmentVariable) ||
                !seenNames.Add(property.EnvironmentVariable))
            {
                continue;
            }

            envVars.Add(new EnvironmentVariableInfo
            {
                Name = property.EnvironmentVariable,
                ConfigPath = section.Name,
                Description = property.Description,
                Default = property.DefaultValue?.ToString()
            });
        }

        return envVars;
    }

    private static IEnumerable<(ConfigurationSection Section, ConfigurationProperty Property)> EnumerateProperties(
        IEnumerable<ConfigurationSection> sections)
    {
        foreach (var section in sections)
        {
            foreach (var property in section.Properties)
            {
                yield return (section, property);
            }

            foreach (var nested in EnumerateProperties(section.SubSections))
            {
                yield return nested;
            }
        }
    }
}
