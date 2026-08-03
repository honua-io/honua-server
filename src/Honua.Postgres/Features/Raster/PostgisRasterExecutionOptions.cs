// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Fail-closed resource and session policy for the dedicated PostGIS raster worker.
/// </summary>
internal sealed class PostgisRasterExecutionOptions
{
    public const string SectionName = "Geoprocessing:Raster:Postgis";
    public const string ConnectionStringName = "RasterPostgis";

    public string RequiredRole { get; set; } = "honua_raster_gp";

    public string SearchPathSchema { get; set; } = "honua";

    public bool RequireTenantSchema { get; set; }

    public int MaxConcurrency { get; set; } = 4;

    public int MaxConcurrencyPerTenant { get; set; } = 2;

    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan IdleInTransactionTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public PostgisRasterWorkLimits WorkLimits { get; set; } = new();

    public Dictionary<string, PostgisRasterTenantPolicy> Tenants { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>Predicted-work ceilings applied before a database connection is acquired.</summary>
internal sealed class PostgisRasterWorkLimits
{
    public long MaxSourceCount { get; set; } = 16;

    public long MaxBandCount { get; set; } = 64;

    public long MaxZoneCount { get; set; } = 1_000_000;

    public long MaxInputPixels { get; set; } = 2_000_000_000;

    public long MaxOutputPixels { get; set; } = 1_000_000_000;

    public long MaxDecodedBytes { get; set; } = 16L * 1024 * 1024 * 1024;

    public long MaxScratchBytes { get; set; } = 32L * 1024 * 1024 * 1024;

    public long MaxDatabaseWork { get; set; } = 4_000_000_000;
}

/// <summary>Optional stricter concurrency and work ceilings for one exact tenant id.</summary>
internal sealed class PostgisRasterTenantPolicy
{
    public int? MaxConcurrency { get; set; }

    public PostgisRasterTenantWorkLimits? WorkLimits { get; set; }
}

/// <summary>Nullable tenant ceilings; omitted values inherit the corresponding global ceiling.</summary>
internal sealed class PostgisRasterTenantWorkLimits
{
    public long? MaxSourceCount { get; set; }

    public long? MaxBandCount { get; set; }

    public long? MaxZoneCount { get; set; }

    public long? MaxInputPixels { get; set; }

    public long? MaxOutputPixels { get; set; }

    public long? MaxDecodedBytes { get; set; }

    public long? MaxScratchBytes { get; set; }

    public long? MaxDatabaseWork { get; set; }
}

internal sealed class PostgisRasterExecutionOptionsValidator
    : IValidateOptions<PostgisRasterExecutionOptions>
{
    public ValidateOptionsResult Validate(string? name, PostgisRasterExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.RequiredRole))
        {
            failures.Add("RequiredRole must name the dedicated raster database role.");
        }

        if (!SchemaSearchPath.IsValidIdentifier(options.SearchPathSchema))
        {
            failures.Add("SearchPathSchema must be a safe PostgreSQL identifier.");
        }

        if (options.MaxConcurrency <= 0)
        {
            failures.Add("MaxConcurrency must be greater than zero.");
        }

        if (options.MaxConcurrencyPerTenant <= 0 ||
            options.MaxConcurrencyPerTenant > options.MaxConcurrency)
        {
            failures.Add("MaxConcurrencyPerTenant must be positive and no greater than MaxConcurrency.");
        }

        ValidatePositiveTimeout(options.QueueTimeout, nameof(options.QueueTimeout), failures);
        ValidatePositiveTimeout(options.StatementTimeout, nameof(options.StatementTimeout), failures);
        ValidatePositiveTimeout(options.LockTimeout, nameof(options.LockTimeout), failures);
        ValidatePositiveTimeout(
            options.IdleInTransactionTimeout,
            nameof(options.IdleInTransactionTimeout),
            failures);
        ValidateWorkLimits(options.WorkLimits, "WorkLimits", failures);

        foreach (var (tenantId, tenantPolicy) in options.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 256)
            {
                failures.Add("Tenant policy keys must contain 1-256 non-whitespace characters.");
                continue;
            }

            if (tenantPolicy is null)
            {
                failures.Add($"Tenant policy '{tenantId}' must not be null.");
                continue;
            }

            if (tenantPolicy.MaxConcurrency is <= 0 ||
                tenantPolicy.MaxConcurrency > options.MaxConcurrency)
            {
                failures.Add(
                    $"Tenant policy '{tenantId}' MaxConcurrency must be positive and no greater than MaxConcurrency.");
            }

            if (tenantPolicy.WorkLimits is not null)
            {
                ValidateTenantWorkLimits(tenantPolicy.WorkLimits, options.WorkLimits, tenantId, failures);
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePositiveTimeout(
        TimeSpan value,
        string name,
        List<string> failures)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            failures.Add($"{name} must be positive and no greater than {int.MaxValue} milliseconds.");
        }
    }

    private static void ValidateWorkLimits(
        PostgisRasterWorkLimits? limits,
        string path,
        List<string> failures)
    {
        if (limits is null)
        {
            failures.Add($"{path} must be configured.");
            return;
        }

        if (limits.MaxSourceCount <= 0 || limits.MaxBandCount <= 0 || limits.MaxZoneCount <= 0 ||
            limits.MaxInputPixels <= 0 || limits.MaxOutputPixels <= 0 ||
            limits.MaxDecodedBytes <= 0 || limits.MaxScratchBytes <= 0 ||
            limits.MaxDatabaseWork <= 0)
        {
            failures.Add($"Every {path} ceiling must be greater than zero.");
        }
    }

    private static void ValidateTenantWorkLimits(
        PostgisRasterTenantWorkLimits tenant,
        PostgisRasterWorkLimits global,
        string tenantId,
        List<string> failures)
    {
        if (IsInvalidTenantCeiling(tenant.MaxSourceCount, global.MaxSourceCount) ||
            IsInvalidTenantCeiling(tenant.MaxBandCount, global.MaxBandCount) ||
            IsInvalidTenantCeiling(tenant.MaxZoneCount, global.MaxZoneCount) ||
            IsInvalidTenantCeiling(tenant.MaxInputPixels, global.MaxInputPixels) ||
            IsInvalidTenantCeiling(tenant.MaxOutputPixels, global.MaxOutputPixels) ||
            IsInvalidTenantCeiling(tenant.MaxDecodedBytes, global.MaxDecodedBytes) ||
            IsInvalidTenantCeiling(tenant.MaxScratchBytes, global.MaxScratchBytes) ||
            IsInvalidTenantCeiling(tenant.MaxDatabaseWork, global.MaxDatabaseWork))
        {
            failures.Add(
                $"Tenant policy '{tenantId}' work ceilings must be positive and may only tighten global ceilings.");
        }
    }

    private static bool IsInvalidTenantCeiling(long? tenantValue, long globalValue) =>
        tenantValue is <= 0 || tenantValue > globalValue;
}
