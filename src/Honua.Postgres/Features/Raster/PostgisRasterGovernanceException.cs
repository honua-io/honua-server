// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Postgres.Features.Raster;

internal sealed class PostgisRasterGovernanceException : Exception
{
    private PostgisRasterGovernanceException(
        RasterProviderExecutionStatus status,
        string errorCode,
        string message,
        bool isRetryable = false)
        : base(message)
    {
        Status = status;
        ErrorCode = errorCode;
        IsRetryable = isRetryable;
    }

    public RasterProviderExecutionStatus Status { get; }

    public string ErrorCode { get; }

    public bool IsRetryable { get; }

    public static PostgisRasterGovernanceException AdmissionTimeout() => new(
        RasterProviderExecutionStatus.CapabilityUnavailable,
        "postgis-raster-admission-timeout",
        "PostGIS raster capacity was not available before the configured queue timeout.",
        isRetryable: true);

    public static PostgisRasterGovernanceException InvalidTenant() => new(
        RasterProviderExecutionStatus.Failed,
        "postgis-raster-invalid-tenant",
        "The raster attempt does not contain a bounded tenant identity.");

    public static PostgisRasterGovernanceException TenantMismatch() => new(
        RasterProviderExecutionStatus.Failed,
        "postgis-raster-tenant-mismatch",
        "The raster attempt tenant does not match its immutable parameter snapshot.");

    public static PostgisRasterGovernanceException InvalidRequest(string field) => new(
        RasterProviderExecutionStatus.Failed,
        "postgis-raster-invalid-request",
        $"The raster attempt contains an invalid {field}.");

    public static PostgisRasterGovernanceException InvalidCost(string field) => new(
        RasterProviderExecutionStatus.Failed,
        "postgis-raster-invalid-cost",
        $"The raster cost estimate contains an invalid {field}.");

    public static PostgisRasterGovernanceException UnknownCost() => new(
        RasterProviderExecutionStatus.CapabilityUnavailable,
        "postgis-raster-cost-unknown",
        "PostGIS raster execution requires a complete non-negative predicted-work estimate.");

    public static PostgisRasterGovernanceException WorkLimitExceeded(string dimension) => new(
        RasterProviderExecutionStatus.CapabilityUnavailable,
        "postgis-raster-work-limit-exceeded",
        $"Predicted PostGIS raster work exceeds the configured {dimension} ceiling.");

    public static PostgisRasterGovernanceException RoleMismatch() => new(
        RasterProviderExecutionStatus.CapabilityUnavailable,
        "postgis-raster-role-mismatch",
        "The dedicated PostGIS raster data source did not authenticate as its required database role.");

    public static PostgisRasterGovernanceException TenantSchemaUnavailable() => new(
        RasterProviderExecutionStatus.CapabilityUnavailable,
        "postgis-raster-tenant-schema-unavailable",
        "A safe database schema could not be resolved for the raster attempt tenant.");

    public RasterProviderExecutionResult ToResult() => Status switch
    {
        RasterProviderExecutionStatus.CapabilityUnavailable =>
            RasterProviderExecutionResult.CapabilityUnavailable(ErrorCode, Message, IsRetryable),
        _ => RasterProviderExecutionResult.Failed(ErrorCode, Message, IsRetryable),
    };
}
