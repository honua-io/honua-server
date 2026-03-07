// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin.Sdk.Clients;
using Honua.Admin.Sdk.Models;

namespace Honua.Admin.Sdk.Services;

/// <summary>
/// Interface for service management operations.
/// </summary>
public interface IServiceManagementClient : IDisposable
{
    /// <summary>
    /// Gets all available services.
    /// </summary>
    Task<IEnumerable<ServiceInfo>> GetServicesAsync(AdminContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed service information.
    /// </summary>
    Task<ServiceDetails> GetServiceDetailsAsync(string serviceId, AdminContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deploys a new service.
    /// </summary>
    Task<ServiceDeploymentResult> DeployServiceAsync(ServiceConfiguration configuration, AdminContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for user management operations.
/// </summary>
public interface IUserManagementClient : IDisposable
{
    /// <summary>
    /// Creates a new user.
    /// </summary>
    Task<UserCreationResult> CreateUserAsync(UserCreateRequest request, AdminContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants service access to a user.
    /// </summary>
    Task<PermissionResult> GrantServiceAccessAsync(string username, string serviceId, PermissionLevel permissionLevel, AdminContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for bulk operations.
/// </summary>
public interface IBulkOperationsClient : IDisposable
{
    /// <summary>
    /// Imports data with progress reporting.
    /// </summary>
    IAsyncEnumerable<AdminProgress> ImportDataAsync(Stream dataStream, BulkImportOptions options, AdminContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports service data.
    /// </summary>
    Task<Stream> ExportServiceDataAsync(string serviceId, BulkExportOptions options, AdminContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for monitoring operations.
/// </summary>
public interface IMonitoringClient : IDisposable
{
    /// <summary>
    /// Gets service health.
    /// </summary>
    Task<ServiceHealth> GetServiceHealthAsync(string serviceId, AdminContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets performance metrics.
    /// </summary>
    Task<PerformanceMetrics> GetServiceMetricsAsync(string serviceId, TimeSpan timeRange, AdminContext context, CancellationToken cancellationToken = default);
}