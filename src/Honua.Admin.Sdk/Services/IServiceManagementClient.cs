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