// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using Honua.Admin.Sdk.Clients;

namespace Honua.Admin.Sdk.Models;

// Service Management Models

/// <summary>
/// Information about a geospatial service.
/// </summary>
public class ServiceInfo
{
    /// <summary>
    /// Service identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the service.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Service description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Service type (e.g., "FeatureServer", "MapServer").
    /// </summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>
    /// Current service status.
    /// </summary>
    public ServiceStatus Status { get; set; }

    /// <summary>
    /// Service endpoint URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Number of layers in the service.
    /// </summary>
    public int LayerCount { get; set; }

    /// <summary>
    /// When the service was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the service was last modified.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Service owner/creator.
    /// </summary>
    public string? Owner { get; set; }
}

/// <summary>
/// Detailed information about a geospatial service.
/// </summary>
public class ServiceDetails : ServiceInfo
{
    /// <summary>
    /// Service configuration.
    /// </summary>
    public ServiceConfiguration Configuration { get; set; } = new();

    /// <summary>
    /// Layer definitions.
    /// </summary>
    public ImmutableArray<LayerInfo> Layers { get; set; } = ImmutableArray<LayerInfo>.Empty;

    /// <summary>
    /// Current performance metrics.
    /// </summary>
    public ServiceMetrics? Metrics { get; set; }

    /// <summary>
    /// Service health status.
    /// </summary>
    public ServiceHealth Health { get; set; } = new();
}

/// <summary>
/// Configuration for deploying a service.
/// </summary>
public class ServiceConfiguration
{
    /// <summary>
    /// Service name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Data source connection string.
    /// </summary>
    public string DataSource { get; set; } = string.Empty;

    /// <summary>
    /// Layer configurations.
    /// </summary>
    public IList<LayerConfiguration> Layers { get; set; } = new List<LayerConfiguration>();

    /// <summary>
    /// Service-level settings.
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();
}

/// <summary>
/// Layer configuration for service deployment.
/// </summary>
public class LayerConfiguration
{
    /// <summary>
    /// Layer name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Database table name.
    /// </summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// Geometry column name.
    /// </summary>
    public string GeometryColumn { get; set; } = string.Empty;

    /// <summary>
    /// Spatial reference system identifier.
    /// </summary>
    public int SpatialReference { get; set; }

    /// <summary>
    /// Layer-specific settings.
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();
}

/// <summary>
/// Information about a service layer.
/// </summary>
public class LayerInfo
{
    /// <summary>
    /// Layer ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Layer name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Geometry type.
    /// </summary>
    public string GeometryType { get; set; } = string.Empty;

    /// <summary>
    /// Feature count.
    /// </summary>
    public long FeatureCount { get; set; }

    /// <summary>
    /// Spatial extent.
    /// </summary>
    public Extent? Extent { get; set; }
}

/// <summary>
/// Spatial extent information.
/// </summary>
public class Extent
{
    /// <summary>
    /// Minimum X coordinate.
    /// </summary>
    public double XMin { get; set; }

    /// <summary>
    /// Minimum Y coordinate.
    /// </summary>
    public double YMin { get; set; }

    /// <summary>
    /// Maximum X coordinate.
    /// </summary>
    public double XMax { get; set; }

    /// <summary>
    /// Maximum Y coordinate.
    /// </summary>
    public double YMax { get; set; }

    /// <summary>
    /// Spatial reference system.
    /// </summary>
    public int SpatialReference { get; set; }
}

/// <summary>
/// Service deployment result.
/// </summary>
public class ServiceDeploymentResult
{
    /// <summary>
    /// Whether deployment was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Deployed service ID.
    /// </summary>
    public string? ServiceId { get; set; }

    /// <summary>
    /// Deployment message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Service URL if successful.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Deployment errors if any.
    /// </summary>
    public IList<string> Errors { get; set; } = new List<string>();
}

/// <summary>
/// Service status enumeration.
/// </summary>
public enum ServiceStatus
{
    /// <summary>
    /// Service is starting up.
    /// </summary>
    Starting,

    /// <summary>
    /// Service is running normally.
    /// </summary>
    Running,

    /// <summary>
    /// Service is stopping.
    /// </summary>
    Stopping,

    /// <summary>
    /// Service is stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// Service has encountered an error.
    /// </summary>
    Error,

    /// <summary>
    /// Service status is unknown.
    /// </summary>
    Unknown
}

// User Management Models

/// <summary>
/// Request to create a new user.
/// </summary>
public class UserCreateRequest
{
    /// <summary>
    /// Username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// User roles.
    /// </summary>
    public IList<string> Roles { get; set; } = new List<string>();

    /// <summary>
    /// Whether to send welcome email.
    /// </summary>
    public bool SendWelcomeEmail { get; set; } = true;
}

/// <summary>
/// Result of user creation.
/// </summary>
public class UserCreationResult
{
    /// <summary>
    /// Whether creation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Created user ID.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Creation message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Errors if any.
    /// </summary>
    public IList<string> Errors { get; set; } = new List<string>();
}

/// <summary>
/// Permission levels for service access.
/// </summary>
public enum PermissionLevel
{
    /// <summary>
    /// No access.
    /// </summary>
    None,

    /// <summary>
    /// Read-only access.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Read and write access.
    /// </summary>
    ReadWrite,

    /// <summary>
    /// Full administrative access.
    /// </summary>
    Admin
}

/// <summary>
/// Result of permission operation.
/// </summary>
public class PermissionResult
{
    /// <summary>
    /// Whether operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Operation message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

// Bulk Operations Models

/// <summary>
/// Options for bulk data import.
/// </summary>
public class BulkImportOptions
{
    /// <summary>
    /// Target service ID.
    /// </summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Target layer ID.
    /// </summary>
    public int LayerId { get; set; }

    /// <summary>
    /// Data format.
    /// </summary>
    public DataFormat DataFormat { get; set; }

    /// <summary>
    /// Batch size for processing.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Validation mode.
    /// </summary>
    public ValidationMode ValidationMode { get; set; } = ValidationMode.Standard;

    /// <summary>
    /// Whether to update existing features.
    /// </summary>
    public bool UpdateExisting { get; set; }

    /// <summary>
    /// Field mapping for data transformation.
    /// </summary>
    public Dictionary<string, string> FieldMapping { get; set; } = new();
}

/// <summary>
/// Options for bulk data export.
/// </summary>
public class BulkExportOptions
{
    /// <summary>
    /// Export format.
    /// </summary>
    public DataFormat Format { get; set; }

    /// <summary>
    /// Whether to include geometry.
    /// </summary>
    public bool IncludeGeometry { get; set; } = true;

    /// <summary>
    /// Target spatial reference system.
    /// </summary>
    public int SpatialReference { get; set; }

    /// <summary>
    /// Fields to include (null = all fields).
    /// </summary>
    public IList<string>? Fields { get; set; }

    /// <summary>
    /// Optional where clause for filtering.
    /// </summary>
    public string? WhereClause { get; set; }
}

/// <summary>
/// Data formats for import/export.
/// </summary>
public enum DataFormat
{
    /// <summary>
    /// GeoJSON format.
    /// </summary>
    GeoJSON,

    /// <summary>
    /// Shapefile format.
    /// </summary>
    Shapefile,

    /// <summary>
    /// CSV format.
    /// </summary>
    CSV,

    /// <summary>
    /// GeoPackage format.
    /// </summary>
    GeoPackage,

    /// <summary>
    /// KML format.
    /// </summary>
    KML
}

/// <summary>
/// Validation modes for import operations.
/// </summary>
public enum ValidationMode
{
    /// <summary>
    /// No validation.
    /// </summary>
    None,

    /// <summary>
    /// Basic validation.
    /// </summary>
    Basic,

    /// <summary>
    /// Standard validation.
    /// </summary>
    Standard,

    /// <summary>
    /// Strict validation.
    /// </summary>
    Strict
}

// Monitoring Models

/// <summary>
/// Service health information.
/// </summary>
public class ServiceHealth
{
    /// <summary>
    /// Overall health status.
    /// </summary>
    public HealthStatus Status { get; set; }

    /// <summary>
    /// Health check timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Response time in milliseconds.
    /// </summary>
    public double ResponseTimeMs { get; set; }

    /// <summary>
    /// Health check details.
    /// </summary>
    public IList<HealthCheckResult> Checks { get; set; } = new List<HealthCheckResult>();
}

/// <summary>
/// Individual health check result.
/// </summary>
public class HealthCheckResult
{
    /// <summary>
    /// Check name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Check status.
    /// </summary>
    public HealthStatus Status { get; set; }

    /// <summary>
    /// Check message.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Check duration.
    /// </summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Health status enumeration.
/// </summary>
public enum HealthStatus
{
    /// <summary>
    /// Healthy.
    /// </summary>
    Healthy,

    /// <summary>
    /// Degraded performance.
    /// </summary>
    Degraded,

    /// <summary>
    /// Unhealthy.
    /// </summary>
    Unhealthy,

    /// <summary>
    /// Unknown status.
    /// </summary>
    Unknown
}

/// <summary>
/// Performance metrics for a service.
/// </summary>
public class PerformanceMetrics
{
    /// <summary>
    /// Metrics time range.
    /// </summary>
    public TimeRange TimeRange { get; set; } = new();

    /// <summary>
    /// Request metrics.
    /// </summary>
    public RequestMetrics Requests { get; set; } = new();

    /// <summary>
    /// Response time metrics.
    /// </summary>
    public ResponseTimeMetrics ResponseTimes { get; set; } = new();

    /// <summary>
    /// Error metrics.
    /// </summary>
    public ErrorMetrics Errors { get; set; } = new();

    /// <summary>
    /// Resource utilization metrics.
    /// </summary>
    public ResourceMetrics Resources { get; set; } = new();
}

/// <summary>
/// Time range for metrics.
/// </summary>
public class TimeRange
{
    /// <summary>
    /// Start time.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// End time.
    /// </summary>
    public DateTime EndTime { get; set; }
}

/// <summary>
/// Request metrics.
/// </summary>
public class RequestMetrics
{
    /// <summary>
    /// Total requests.
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Requests per second (average).
    /// </summary>
    public double RequestsPerSecond { get; set; }

    /// <summary>
    /// Peak requests per second.
    /// </summary>
    public double PeakRequestsPerSecond { get; set; }
}

/// <summary>
/// Response time metrics.
/// </summary>
public class ResponseTimeMetrics
{
    /// <summary>
    /// Average response time in milliseconds.
    /// </summary>
    public double AverageMs { get; set; }

    /// <summary>
    /// 95th percentile response time.
    /// </summary>
    public double P95Ms { get; set; }

    /// <summary>
    /// 99th percentile response time.
    /// </summary>
    public double P99Ms { get; set; }

    /// <summary>
    /// Maximum response time.
    /// </summary>
    public double MaxMs { get; set; }
}

/// <summary>
/// Error metrics.
/// </summary>
public class ErrorMetrics
{
    /// <summary>
    /// Total errors.
    /// </summary>
    public long TotalErrors { get; set; }

    /// <summary>
    /// Error rate (percentage).
    /// </summary>
    public double ErrorRate { get; set; }

    /// <summary>
    /// Errors by status code.
    /// </summary>
    public Dictionary<int, long> ErrorsByStatusCode { get; set; } = new();
}

/// <summary>
/// Resource utilization metrics.
/// </summary>
public class ResourceMetrics
{
    /// <summary>
    /// CPU usage percentage.
    /// </summary>
    public double CpuUsagePercent { get; set; }

    /// <summary>
    /// Memory usage in bytes.
    /// </summary>
    public long MemoryUsageBytes { get; set; }

    /// <summary>
    /// Disk usage percentage.
    /// </summary>
    public double DiskUsagePercent { get; set; }

    /// <summary>
    /// Network I/O bytes per second.
    /// </summary>
    public long NetworkBytesPerSecond { get; set; }
}

/// <summary>
/// Service metrics summary.
/// </summary>
public class ServiceMetrics
{
    /// <summary>
    /// Current request count.
    /// </summary>
    public long CurrentRequests { get; set; }

    /// <summary>
    /// Average response time.
    /// </summary>
    public double AverageResponseTime { get; set; }

    /// <summary>
    /// Error count in the last hour.
    /// </summary>
    public long RecentErrors { get; set; }

    /// <summary>
    /// Uptime duration.
    /// </summary>
    public TimeSpan Uptime { get; set; }
}